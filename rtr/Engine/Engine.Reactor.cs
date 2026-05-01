using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.ObjectPool;
using zerg.core;
using rtr.Engine.Configs;
using static rtr.ABI.ABI;

// ReSharper disable always CheckNamespace
// ReSharper disable always SuggestVarOrType_BuiltInTypes

namespace rtr.Engine;

public sealed unsafe partial class Engine
{
    /// <summary>
    /// Object pool for Connection instances.
    /// </summary>
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

    /// <summary>
    /// A reactor owns:
    ///  - one io_uring instance
    ///  - its own SO_REUSEPORT listening socket
    ///  - the multishot accept that delivers new client fds onto its own ring
    ///  - the buffer ring lifecycle and recv/send CQE processing
    /// </summary>
    public partial class Reactor : IReactor
    {
        private io_uring_buf_ring* _bufferRing;
        private byte* _bufferRingSlab;
        private uint _bufferRingIndex;
        private uint _bufferRingMask;
        private readonly Engine _engine;
        private readonly HashSet<int> _flushableFds = [];

        /// <summary>
        /// Listening socket fd owned by this reactor (created with SO_REUSEPORT).
        /// </summary>
        private int _listenFd = -1;

        public Reactor(int id, ReactorConfig config, Engine engine)
        {
            Id = id;
            Config = config;
            _engine = engine;
        }

        public Reactor(int id, Engine engine) : this(id, new ReactorConfig(), engine) { }

        public int Id { get; }
        public ReactorConfig Config { get; }
        public io_uring* io_uring_instance { get; private set; }

        /// <summary>
        /// Creates the io_uring instance, the SO_REUSEPORT listening socket, arms multishot
        /// accept on this reactor's ring, and registers the buffer ring.
        /// </summary>
        public void InitRing()
        {
            io_uring_instance = CreateRing(Config.RingFlags, Config.SqCpuThread, Config.SqThreadIdleMs, out int err, Config.RingEntries);
            if (io_uring_instance == null || err != 0)
            {
                Console.WriteLine($"create_ring failed: {err}");
                return;
            }

            uint ringFlags = shim_get_ring_flags(io_uring_instance);
            Console.WriteLine($"[w{Id}] ring flags = 0x{ringFlags:x} " +
                              $"(SQPOLL={(ringFlags & IORING_SETUP_SQPOLL) != 0}, " +
                              $"SQ_AFF={(ringFlags & IORING_SETUP_SQ_AFF) != 0})");

            _listenFd = _engine.Options.IPVersion == IPVersion.IPv4Only
                ? CreateIPv4ListenerSocket(_engine.Options.Ip, _engine.Options.Port)
                : CreateListenerSocketDualStack(_engine.Options.Ip, _engine.Options.Port);

            io_uring_sqe* acceptSqe = SqeGet(io_uring_instance);
            shim_prep_multishot_accept(acceptSqe, _listenFd, SOCK_NONBLOCK);
            shim_sqe_set_data64(acceptSqe, PackUd(UdKind.Accept, _listenFd));
            shim_submit(io_uring_instance);
            Console.WriteLine($"[w{Id}] Multishot accept armed on fd={_listenFd}");

            if (!Config.IncrementalBufferConsumption)
            {
                _bufferRing = shim_setup_buf_ring(io_uring_instance, (uint)Config.BufferRingEntries, c_bufferRingGID, 0u, out var ret);
                if (_bufferRing == null || ret < 0)
                    throw new Exception($"setup_buf_ring failed: ret={ret}");

                _bufferRingMask = (uint)(Config.BufferRingEntries - 1);
                nuint slabSize = (nuint)(Config.BufferRingEntries * Config.RecvBufferSize);
                _bufferRingSlab = (byte*)NativeMemory.AlignedAlloc(slabSize, 64);

                for (ushort bid = 0; bid < Config.BufferRingEntries; bid++)
                {
                    byte* addr = _bufferRingSlab + (nuint)bid * (nuint)Config.RecvBufferSize;
                    shim_buf_ring_add(_bufferRing, addr, (uint)Config.RecvBufferSize, bid, (ushort)_bufferRingMask, _bufferRingIndex++);
                }
                shim_buf_ring_advance(_bufferRing, (uint)Config.BufferRingEntries);
            }
            else
            {
                InitRingIncremental();
            }
        }

        private void ReturnBufferRing(byte* addr, ushort bid)
        {
            shim_buf_ring_add(_bufferRing, addr, (uint)Config.RecvBufferSize, bid, (ushort)_bufferRingMask, _bufferRingIndex++);
            shim_buf_ring_advance(_bufferRing, 1);
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
            io_uring_sqe* sqe = SqeGet(io_uring_instance);
            shim_prep_send(sqe, clientFd, buf + off, (uint)(len - off), 0);
            shim_sqe_set_data64(sqe, PackUd(UdKind.Send, clientFd));
        }

        private static void SubmitSend(io_uring* pring, int fd, byte* buf, nuint off, nuint len)
        {
            io_uring_sqe* sqe = SqeGet(pring);
            shim_prep_send(sqe, fd, buf + off, (uint)(len - off), 0);
            shim_sqe_set_data64(sqe, PackUd(UdKind.Send, fd));
        }

        private static void SubmitCancelRecv(io_uring* ring, int fd)
        {
            io_uring_sqe* sqe = shim_get_sqe(ring);
            if (sqe == null) return;

            ulong target = PackUd(UdKind.Recv, fd);

            shim_prep_cancel64(sqe, target, 0);
            shim_sqe_set_data64(sqe, PackUd(UdKind.Cancel, fd));
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
                } catch { /* ignore */ }

                _engine.ConnectionPool.Return(conn);
            }

            connections.Clear();
        }

        /// <summary>
        /// Closes the per-reactor listening socket. Called from the reactor's finally block.
        /// </summary>
        private void CloseListener()
        {
            if (_listenFd >= 0)
            {
                try { close(_listenFd); } catch { /* ignore */ }
                _listenFd = -1;
            }
        }
    }
}
