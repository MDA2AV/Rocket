using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.ObjectPool;
using zerg.Engine.Configs;
using zerg.Utils.MultiProducerSingleConsumer;
using static zerg.ABI.ABI;

// ReSharper disable always CheckNamespace
// ReSharper disable always SuggestVarOrType_BuiltInTypes
// (var is avoided intentionally in this project so that concrete types are visible at call sites.)

namespace zerg.Engine;

public sealed unsafe partial class Engine
{
    /// <summary>
    /// Object pool for Connection instances.
    /// Avoids per-connection allocations and allows reuse across reactors.
    /// </summary>
    private readonly ObjectPool<Connection> ConnectionPool =
        new DefaultObjectPool<Connection>(new ConnectionPoolPolicy(), 1024 * 32);
    /// <summary> Pool policy for Connection objects. </summary>
    private class ConnectionPoolPolicy : PooledObjectPolicy<Connection>
    {
        /// <summary>Create a new connection instance when the pool is empty.</summary>
        public override Connection Create() => new();
        /// <summary> Reset and return the connection to the pool. </summary>
        public override bool Return(Connection connection)
        {
            connection.Clear();
            return true;
        }
    }

    /// <summary>
    /// A reactor owns one io_uring instance and processes:
    ///  - recv/send CQEs
    ///  - connection state
    ///  - buffer ring lifecycle
    /// Each reactor runs on its own thread.
    /// </summary>
    public partial class Reactor
    {
        /// <summary>
        /// Kernel-registered io_uring buffer ring for multishot recv.
        /// </summary>
        private io_uring_buf_ring* _bufferRing;
        /// <summary>
        /// Slab backing all provided recv buffers.
        /// Each buffer is RecvBufferSize bytes.
        /// </summary>
        private byte* _bufferRingSlab;
        /// <summary>
        /// Next position in the buffer ring.
        /// </summary>
        private uint _bufferRingIndex;
        /// <summary>
        /// Mask for buffer ring indexing (power-of-two entries).
        /// </summary>
        private uint _bufferRingMask;
        /// <summary>
        /// Back-reference to owning engine.
        /// </summary>
        private readonly Engine _engine;
        /// <summary>
        /// Tracks which connections need flushing after batched writes.
        /// </summary>
        private readonly HashSet<int> _flushableFds = [];

        // =========================================================================
        // Per-connection buffer ring state (incremental mode)
        // =========================================================================

        private bool _incrementalMode;
        private Stack<ushort>? _freeGids;
        private MpscUlongQueue? _returnQInc;

        public Reactor(int id, ReactorConfig config, Engine engine)
        {
            Id = id;
            Config = config;
            _engine = engine;
        }

        public Reactor(int id, Engine engine) : this(id, new ReactorConfig(), engine) { }

        /// <summary>Reactor ID (index into engine arrays).</summary>
        public int Id { get; }

        /// <summary>Configuration for this reactor.</summary>
        public ReactorConfig Config { get; }

        /// <summary>io_uring instance owned by this reactor.</summary>
        public io_uring* io_uring_instance { get; private set; }

        /// <summary>
        /// Creates the io_uring instance and registers the buffer ring.
        /// Also allocates and registers all recv buffers.
        /// </summary>
        public void InitRing()
        {
            io_uring_instance = CreateRing(Config.RingFlags, Config.SqCpuThread, Config.SqThreadIdleMs, out int err, Config.RingEntries);
            if (io_uring_instance == null || err != 0)
            {
                Console.WriteLine($"create_ring failed: {err}");
                return; // or throw; but do NOT continue using ring
            }

            uint ringFlags = shim_get_ring_flags(io_uring_instance);
            Console.WriteLine($"[w{Id}] ring flags = 0x{ringFlags:x} " +
                              $"(SQPOLL={(ringFlags & IORING_SETUP_SQPOLL) != 0}, " +
                              $"SQ_AFF={(ringFlags & IORING_SETUP_SQ_AFF) != 0})");
            if (io_uring_instance == null || err < 0)
            {
                Console.Error.WriteLine($"[w{Id}] create_ring failed: {err}");
                return;
            }

            _incrementalMode = Config.IncrementalBufferConsumption;

            if (!_incrementalMode)
            {
                // Shared buffer ring (existing behavior)
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
                // Incremental mode: per-connection rings, no shared ring.
                // GID 1 is reserved (shared ring slot, unused). Per-connection GIDs start at 2.
                _freeGids = new Stack<ushort>(Config.MaxConnectionsPerReactor);
                for (int g = Config.MaxConnectionsPerReactor + 1; g >= 2; g--)
                    _freeGids.Push((ushort)g);

                _returnQInc = new MpscUlongQueue(1 << 16);
            }
        }
        /// <summary>
        /// Returns a previously used recv buffer back to the kernel buf_ring.
        /// </summary>
        private void ReturnBufferRing(byte* addr, ushort bid)
        {
            shim_buf_ring_add(_bufferRing, addr, (uint)Config.RecvBufferSize, bid, (ushort)_bufferRingMask, _bufferRingIndex++);
            shim_buf_ring_advance(_bufferRing, 1);
        }
        /// <summary>
        /// MPSC queue of buffer IDs waiting to be returned to buf_ring.
        /// </summary>
        private readonly MpscUshortQueue _returnQ = new(1 << 16); // 65536 slots (power-of-two)
        /// <summary>
        /// Enqueue a buffer ID to be returned to the buf_ring.
        /// May spin briefly under contention.
        /// </summary>
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
                            return; // only bail out after trying
                        Thread.Yield();
                        sw.Reset();
                    }
                }
            }
        }
        /// <summary>
        /// Drains the return queue and re-adds buffers to the buf_ring.
        /// </summary>
        private void DrainReturnQ()
        {
            while (_returnQ.TryDequeue(out ushort bid))
            {
                byte* addr = _bufferRingSlab + (nuint)bid * (nuint)Config.RecvBufferSize;
                ReturnBufferRing(addr, bid);
            }
        }
        /// <summary>
        /// Same as DrainReturnQ but returns how many buffers were recycled.
        /// </summary>
        private int DrainReturnQCounted()
        {
            int count = 0;
            while (_returnQ.TryDequeue(out ushort bid))
            {
                byte* addr = _bufferRingSlab + (nuint)bid * (nuint)Config.RecvBufferSize;
                ReturnBufferRing(addr, bid);
                count++;
            }
            return count;
        }

        // =========================================================================
        // Per-connection buffer ring lifecycle (incremental mode)
        // =========================================================================

        private ushort AllocGid() => _freeGids!.Pop();
        private void FreeGid(ushort gid) => _freeGids!.Push(gid);

        private void SetupConnectionBufRing(Connection conn)
        {
            ushort gid = AllocGid();
            int entries = Config.ConnectionBufferRingEntries;

            io_uring_buf_ring* ring = shim_setup_buf_ring(
                io_uring_instance, (uint)entries, gid, IOU_PBUF_RING_INC, out int ret);
            if (ring == null || ret < 0)
                throw new Exception($"setup_buf_ring (per-conn) failed: ret={ret} gid={gid}");

            // Allocate slab if not already allocated from a previous pool lifetime
            if (conn.BufRingSlab == null)
            {
                nuint slabSize = (nuint)(entries * Config.RecvBufferSize);
                conn.BufRingSlab = (byte*)NativeMemory.AlignedAlloc(slabSize, 64);
            }

            // Allocate tracking arrays if needed
            conn.BufRefCounts ??= new int[entries];
            conn.BufKernelDone ??= new bool[entries];
            conn.BufCumulativeOffset ??= new int[entries];

            // Reset tracking state
            Array.Clear(conn.BufRefCounts, 0, entries);
            Array.Clear(conn.BufKernelDone, 0, entries);
            Array.Clear(conn.BufCumulativeOffset, 0, entries);

            conn.BufRing = ring;
            conn.BufRingEntries = entries;
            conn.BufRingMask = (uint)(entries - 1);
            conn.BufRingIndex = 0;
            conn.Bgid = gid;
            conn.IncrementalMode = true;

            // Populate ring with buffers
            for (ushort bid = 0; bid < entries; bid++)
            {
                byte* addr = conn.BufRingSlab + (nuint)bid * (nuint)Config.RecvBufferSize;
                shim_buf_ring_add(ring, addr, (uint)Config.RecvBufferSize, bid, (ushort)conn.BufRingMask, conn.BufRingIndex++);
            }
            shim_buf_ring_advance(ring, (uint)entries);
        }

        private void TeardownConnectionBufRing(Connection conn)
        {
            if (conn.BufRing != null)
            {
                shim_free_buf_ring(io_uring_instance, conn.BufRing, (uint)conn.BufRingEntries, conn.Bgid);
                conn.BufRing = null;
            }
            FreeGid(conn.Bgid);
            // Slab and arrays stay allocated for pool reuse
        }

        private void ReturnConnectionBuffer(Connection conn, ushort bid)
        {
            // Reset tracking for this bid
            conn.BufRefCounts![bid] = 0;
            conn.BufKernelDone![bid] = false;
            conn.BufCumulativeOffset![bid] = 0;

            byte* addr = conn.BufRingSlab + (nuint)bid * (nuint)Config.RecvBufferSize;
            shim_buf_ring_add(conn.BufRing, addr, (uint)Config.RecvBufferSize, bid, (ushort)conn.BufRingMask, conn.BufRingIndex++);
            shim_buf_ring_advance(conn.BufRing, 1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EnqueueReturnQIncremental(int fd, ushort bid)
        {
            ulong packed = MpscUlongQueue.Pack(fd, bid);
            if (!_returnQInc!.TryEnqueue(packed))
            {
                SpinWait sw = default;
                while (!_returnQInc.TryEnqueue(packed))
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

        private void DrainReturnQIncremental()
        {
            Dictionary<int, Connection> connections = _engine.Connections[Id];
            while (_returnQInc!.TryDequeue(out ulong packed))
            {
                MpscUlongQueue.Unpack(packed, out int fd, out ushort bid);

                if (!connections.TryGetValue(fd, out Connection? conn) || !conn.IncrementalMode)
                    continue; // fd gone or ring already torn down

                if (conn.BufRefCounts![bid] <= 0)
                    continue; // stale return (fd reuse guard)

                conn.BufRefCounts[bid]--;

                if (conn.BufRefCounts[bid] == 0 && conn.BufKernelDone![bid])
                    ReturnConnectionBuffer(conn, bid);
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

                // If already have a send in flight, do nothing:
                // the CQE path will continue draining to WriteInFlight.
                if (Volatile.Read(ref c.SendInflight) != 0)
                    continue;

                // If nothing to flush (or flush target not set), do nothing.
                // FlushAsync sets WriteInFlight = tail snapshot.
                int target = c.WriteInFlight;
                if (target <= 0)
                    continue;

                // Start sending from current head (should be 0 for slab reset model)
                // If you ever keep head across flushes, this still works.
                if (c.WriteHead >= target)
                {
                    // already satisfied
                    if (c.IsFlushInProgress)
                        c.CompleteFlush();
                    continue;
                }

                Volatile.Write(ref c.SendInflight, 1);

                Send(c.ClientFd, c.WriteBuffer, (uint)c.WriteHead, (uint)target);
            }
        }

        /// <summary>
        /// Enqueue a send SQE for this reactor's ring.
        /// </summary>
        private void Send(int clientFd, byte* buf, nuint off, nuint len)
        {
            io_uring_sqe* sqe = SqeGet(io_uring_instance);
            shim_prep_send(sqe, clientFd, buf + off, (uint)(len - off), 0);
            shim_sqe_set_data64(sqe, PackUd(UdKind.Send, clientFd));
        }
        /// <summary>
        /// Static helper to enqueue a send on an arbitrary ring.
        /// </summary>
        private static void SubmitSend(io_uring* pring, int fd, byte* buf, nuint off, nuint len)
        {
            io_uring_sqe* sqe = SqeGet(pring);
            shim_prep_send(sqe, fd, buf + off, (uint)(len - off), 0);
            shim_sqe_set_data64(sqe, PackUd(UdKind.Send, fd));
        }
        /// <summary>
        /// Cancels an outstanding multishot recv for a given fd.
        /// </summary>
        private static void SubmitCancelRecv(io_uring* ring, int fd)
        {
            io_uring_sqe* sqe = shim_get_sqe(ring);
            if (sqe == null) return; // or handle SQ full

            ulong target = PackUd(UdKind.Recv, fd);

            shim_prep_cancel64(sqe, target, /*flags*/ 0 /* or IORING_ASYNC_CANCEL_ALL */);
            shim_sqe_set_data64(sqe, PackUd(UdKind.Cancel, fd));
        }
        /// <summary>
        /// Closes all remaining connections during shutdown and
        /// returns them to the pool.
        /// </summary>
        private void CloseAll(Dictionary<int, Connection> connections)
        {
            foreach (var kv in connections)
            {
                var conn = kv.Value;

                // Mark closed to wake any waiter.
                conn.MarkClosed(error: 0);

                // Teardown per-connection buffer ring if in incremental mode
                if (conn.IncrementalMode)
                    TeardownConnectionBufRing(conn);

                // Close fd
                try
                {
                    close(conn.ClientFd);
                } catch { /* ignore */ }

                _engine.ConnectionPool.Return(conn);
            }

            connections.Clear();
        }
    }
}
