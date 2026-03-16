using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.ObjectPool;
using terraform.Engine.Configs;
using terraform.Ring;
using terraform.Utils.MultiProducerSingleConsumer;
using static terraform.ABI.Libc;
using static terraform.Ring.Constants;

namespace terraform.Engine;

public sealed unsafe partial class Engine
{
    private readonly ObjectPool<Connection> ConnectionPool =
        new DefaultObjectPool<Connection>(new ConnectionPoolPolicy(), 1024 * 32);

    private class ConnectionPoolPolicy : PooledObjectPolicy<Connection>
    {
        public override Connection Create() => new();
        public override bool Return(Connection connection)
        {
            connection.Clear();
            return true;
        }
    }

    public partial class Reactor
    {
        private IoUringBuf* _bufferRing;
        private byte* _bufferRingSlab;
        private uint _bufferRingIndex;
        private uint _bufferRingMask;
        private readonly Engine _engine;
        private readonly HashSet<int> _flushableFds = [];

        public Reactor(int id, ReactorConfig config, Engine engine)
        {
            Id = id;
            Config = config;
            _engine = engine;
        }

        public Reactor(int id, Engine engine) : this(id, new ReactorConfig(), engine) { }

        public int Id { get; }
        public ReactorConfig Config { get; }
        internal IoUring Ring { get; set; } = null!;

        public void InitRing()
        {
            Ring = CreateRing(Config.RingFlags, Config.SqCpuThread, Config.SqThreadIdleMs, Config.RingEntries);

            uint ringFlags = Ring.SetupFlags;
            Console.WriteLine($"[w{Id}] ring flags = 0x{ringFlags:x} " +
                              $"(SQPOLL={(ringFlags & IORING_SETUP_SQPOLL) != 0}, " +
                              $"SQ_AFF={(ringFlags & IORING_SETUP_SQ_AFF) != 0})");

            // Shared buffer ring
            _bufferRing = BufRing.Setup(Ring, (uint)Config.BufferRingEntries, c_bufferRingGID);

            _bufferRingMask = (uint)(Config.BufferRingEntries - 1);
            nuint slabSize = (nuint)(Config.BufferRingEntries * Config.RecvBufferSize);
            _bufferRingSlab = (byte*)NativeMemory.AlignedAlloc(slabSize, 64);

            for (ushort bid = 0; bid < Config.BufferRingEntries; bid++)
            {
                byte* addr = _bufferRingSlab + (nuint)bid * (nuint)Config.RecvBufferSize;
                BufRing.Add(_bufferRing, addr, (uint)Config.RecvBufferSize, bid, (ushort)_bufferRingMask, _bufferRingIndex++);
            }
            BufRing.Advance(_bufferRing, (uint)Config.BufferRingEntries);
        }

        private void ReturnBufferRing(byte* addr, ushort bid)
        {
            BufRing.Add(_bufferRing, addr, (uint)Config.RecvBufferSize, bid, (ushort)_bufferRingMask, _bufferRingIndex++);
            BufRing.Advance(_bufferRing, 1);
        }

        private readonly MpscUshortQueue _returnQ = new(1 << 16);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EnqueueReturnQ(ushort bid)
        {
            if (!_returnQ.TryEnqueue(bid))
            {
                SpinWait sw = default;
                while (!_returnQ.TryEnqueue(bid))
                {
                    sw.SpinOnce();
                    if (sw.Count > 50)
                    {
                        if (!_engine.ServerRunning)
                            return;
                        Thread.Yield();
                        sw.Reset();
                    }
                }
            }
        }

        private void DrainReturnQ()
        {
            while (_returnQ.TryDequeue(out ushort bid))
            {
                byte* addr = _bufferRingSlab + (nuint)bid * (nuint)Config.RecvBufferSize;
                ReturnBufferRing(addr, bid);
            }
        }

        private readonly MpscIntQueue _flushQ = new(capacityPow2: 4096);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EnqueueFlush(int fd)
        {
            while (!_flushQ.TryEnqueue(fd))
                Thread.Yield();
        }

        private void DrainFlushQ()
        {
            while (_flushQ.TryDequeue(out int fd))
            {
                if (!_engine.Connections[Id].TryGetValue(fd, out var c))
                    continue;

                if (Volatile.Read(ref c.SendInflight) != 0)
                    continue;

                int target = c.WriteInFlight;
                if (target <= 0)
                    continue;

                if (c.WriteHead >= target)
                {
                    if (c.IsFlushInProgress)
                        c.CompleteFlush();
                    continue;
                }

                Volatile.Write(ref c.SendInflight, 1);

                Send(c.ClientFd, c.WriteBuffer, (uint)c.WriteHead, (uint)target);
            }
        }

        private void Send(int clientFd, byte* buf, nuint off, nuint len)
        {
            IoUringSqe* sqe = SqeGet(Ring);
            Sqe.PrepSend(sqe, clientFd, buf + (nint)off, (uint)(len - off), 0);
            sqe->user_data = PackUd(UdKind.Send, clientFd);
        }

        private void SubmitSend(int fd, byte* buf, nuint off, nuint len)
        {
            IoUringSqe* sqe = SqeGet(Ring);
            Sqe.PrepSend(sqe, fd, buf + (nint)off, (uint)(len - off), 0);
            sqe->user_data = PackUd(UdKind.Send, fd);
        }

        private void SubmitCancelRecv(int fd)
        {
            IoUringSqe* sqe = Ring.GetSqe();
            if (sqe == null) return;

            ulong target = PackUd(UdKind.Recv, fd);

            Sqe.PrepCancel64(sqe, target, 0);
            sqe->user_data = PackUd(UdKind.Cancel, fd);
        }

        private void CloseAll(Dictionary<int, Connection> connections)
        {
            foreach (var kv in connections)
            {
                var conn = kv.Value;
                conn.MarkClosed(error: 0);

                try
                {
                    close(conn.ClientFd);
                } catch { }

                _engine.ConnectionPool.Return(conn);
            }

            connections.Clear();
        }
    }
}
