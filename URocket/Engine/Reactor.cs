using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.ObjectPool;
using URocket.Engine.Configs;
using static URocket.ABI.ABI;

// ReSharper disable always CheckNamespace
// ReSharper disable always SuggestVarOrType_BuiltInTypes
// (var is avoided intentionally in this project so that concrete types are visible at call sites.)

namespace URocket.Engine;

public sealed unsafe partial class Engine {
    private readonly ObjectPool<Connection> ConnectionPool =
        new DefaultObjectPool<Connection>(new ConnectionPoolPolicy(), 1024 * 32);

    private class ConnectionPoolPolicy : PooledObjectPolicy<Connection> {
        public override Connection Create() => new();
        public override bool Return(Connection connection) { connection.Clear(); return true; }
    }
    
    public Reactor[] Reactors = null!;
    public Dictionary<int, Connection>[] Connections = null!;
    
    public class Reactor {
        private int _counter;
        private io_uring_buf_ring* _bufferRing;
        private byte* _bufferRingSlab;
        private uint _bufferRingIndex;
        private uint _bufferRingMask;

        private readonly Engine _engine;

        public Reactor(int id, ReactorConfig config, Engine engine) {
            Id = id; 
            Config = config; 
            _engine = engine;
        }
        public int Id { get; }
        public Reactor(int id, Engine engine) : this(id, new ReactorConfig(), engine) { }
        public ReactorConfig Config { get; }
        public io_uring* Ring { get; private set; }

        public void InitRing() {
            Ring = CreateRing(Config.RingFlags, Config.SqCpuThread, Config.SqThreadIdleMs, out int err, Config.RingEntries);
            uint ringFlags = shim_get_ring_flags(Ring);
            Console.WriteLine($"[w{Id}] ring flags = 0x{ringFlags:x} " +
                              $"(SQPOLL={(ringFlags & IORING_SETUP_SQPOLL) != 0}, " +
                              $"SQ_AFF={(ringFlags & IORING_SETUP_SQ_AFF) != 0})");
            if (Ring == null || err < 0) { Console.Error.WriteLine($"[w{Id}] create_ring failed: {err}"); return; }
            
            _bufferRing = shim_setup_buf_ring(Ring, (uint)Config.BufferRingEntries, c_bufferRingGID, 0, out var ret);
            if (_bufferRing == null || ret < 0) throw new Exception($"setup_buf_ring failed: ret={ret}");

            _bufferRingMask = (uint)(Config.BufferRingEntries - 1);
            nuint slabSize = (nuint)(Config.BufferRingEntries * Config.RecvBufferSize);
            _bufferRingSlab = (byte*)NativeMemory.AlignedAlloc(slabSize, 64);

            for (ushort bid = 0; bid < Config.BufferRingEntries; bid++) {
                byte* addr = _bufferRingSlab + (nuint)bid * (nuint)Config.RecvBufferSize;
                shim_buf_ring_add(_bufferRing, addr, (uint)Config.RecvBufferSize, bid, (ushort)_bufferRingMask, _bufferRingIndex++);
            }
            shim_buf_ring_advance(_bufferRing, (uint)Config.BufferRingEntries);
        }

        public void ReturnBufferRing(byte* addr, ushort bid) {
            shim_buf_ring_add(_bufferRing, addr, (uint)Config.RecvBufferSize, bid, (ushort)_bufferRingMask, _bufferRingIndex++);
            shim_buf_ring_advance(_bufferRing, 1);
        }
        
        internal void Handle() {
            Dictionary<int,Connection> connections = _engine.Connections[Id];
            ConcurrentQueue<int> reactorQueue = ReactorQueues[Id];     // new FDs from acceptor
            io_uring_cqe*[] cqes = new io_uring_cqe*[Config.BatchCqes];

            try {
                while (_engine.ServerRunning) {
                    while (reactorQueue.TryDequeue(out int newFd)) { ArmRecvMultishot(Ring, newFd, c_bufferRingGID); }
                    if (shim_sq_ready(Ring) > 0) shim_submit(Ring);
                    io_uring_cqe* cqe; __kernel_timespec ts; ts.tv_sec  = 0; ts.tv_nsec = Config.CqTimeout; // 1 ms timeout
                    int rc = shim_wait_cqes(Ring, &cqe, (uint)1, &ts); int got;
                    
                    if (rc is -62 or < 0) { _counter++; continue; }

                    fixed (io_uring_cqe** pC = cqes) got = shim_peek_batch_cqe(Ring, pC, (uint)Config.BatchCqes);

                    for (int i = 0; i < got; i++) {
                        cqe = cqes[i];
                        ulong ud = shim_cqe_get_data64(cqe);
                        UdKind kind = UdKindOf(ud);
                        int res  = cqe->res;

                        if (kind == UdKind.Recv) {
                            int fd = UdFdOf(ud);
                            bool hasBuffer = shim_cqe_has_buffer(cqe) != 0;
                            bool hasMore   = (cqe->flags & IORING_CQE_F_MORE) != 0;

                            if (res <= 0) {
                                Console.WriteLine($"{Id} {_counter}");
                                if (hasBuffer) {
                                    ushort bufferId = (ushort)shim_cqe_buffer_id(cqe);
                                    byte* addr = _bufferRingSlab + (nuint)bufferId * (nuint)Config.RecvBufferSize;
                                    shim_buf_ring_add(_bufferRing, addr, (uint)Config.RecvBufferSize, bufferId, (ushort)_bufferRingMask, _bufferRingIndex++);
                                    shim_buf_ring_advance(_bufferRing, 1);
                                }
                                if (connections.TryGetValue(fd, out var connection)) {
                                    _engine.ConnectionPool.Return(connection);
                                    close(fd);
                                }
                            } else {
                                var bufferId = (ushort)shim_cqe_buffer_id(cqe);

                                // TODO: Issue found, what if this triggers again before the client handles it, data is lost
                                if (connections.TryGetValue(fd, out var connection)) {
                                    connection.HasBuffer = hasBuffer;
                                    connection.BufferId = bufferId;
                                    connection.InPtr = _bufferRingSlab + (nuint)connection.BufferId * (nuint)Config.RecvBufferSize;
                                    connection.InLength = res;
                                    connection.SignalReadReady();
                                    
                                    if (!hasMore) ArmRecvMultishot(Ring, fd, c_bufferRingGID);
                                }
                            }
                        }
                        else if (kind == UdKind.Send) {
                            int fd = UdFdOf(ud);
                            if (connections.TryGetValue(fd, out var connection)) {
                                // Advance send progress.
                                connection.OutHead += (nuint)res;
                                if (connection.OutHead < connection.OutTail)
                                    SubmitSend(Ring, connection.ClientFd, connection.OutPtr, connection.OutHead, connection.OutTail);
                            }
                        }
                        shim_cqe_seen(Ring, cqe);
                    }
                }
            } finally {
                // Close any remaining connections
                CloseAll(connections);
                // Free buffer ring BEFORE destroying the ring
                if (Ring != null && _bufferRing != null) {
                    shim_free_buf_ring(Ring, _bufferRing, (uint)Config.BufferRingEntries, c_bufferRingGID);
                    _bufferRing = null;
                }
                // Destroy ring (unregisters CQ/SQ memory mappings)
                if (Ring != null) { shim_destroy_ring(Ring); Ring = null; }
                // Free slab memory used by buf ring
                if (_bufferRingSlab != null) { NativeMemory.AlignedFree(_bufferRingSlab); _bufferRingSlab = null; }
                Console.WriteLine($"[w{Id}] Shutdown complete.");
            }
        }
        
        // Experimental
        private void HandleSQPoll() {
            Dictionary<int, Connection> connections = _engine.Connections[Id];
            ConcurrentQueue<int> myQueue = ReactorQueues[Id]; // new FDs from acceptor
            io_uring_cqe*[] cqes = new io_uring_cqe*[Config.BatchCqes];
            
            __kernel_timespec ts; ts.tv_sec = 0; ts.tv_nsec = Config.CqTimeout;

            // Optional: if shim exposes this, cache whether SQPOLL is enabled for this ring
            // (purely for metrics / readability; submit logic should still key off NEED_WAKEUP).
            uint ringSetupFlags = Ring != null ? shim_get_ring_flags(Ring) : 0;
            bool isSqPoll = (ringSetupFlags & IORING_SETUP_SQPOLL) != 0;

            try {
                while (_engine.ServerRunning) {
                    // Track whether we queued any SQEs this iteration.
                    bool queuedSqe = false;

                    // Drain acceptor queue and arm multishot recv for each new fd.
                    while (myQueue.TryDequeue(out int newFd)) {
                        ArmRecvMultishot(Ring, newFd, c_bufferRingGID);
                        queuedSqe = true;
                    }

                    // Submit only if we actually queued work (or if your ring says SQEs are pending).
                    //  (If Arm* methods can fail to get an SQE and we defer, keep shim_sq_ready too.)
                    if (queuedSqe || shim_sq_ready(Ring) > 0) {
                        // IMPORTANT for SQPOLL:
                        // shim_submit() MUST do the equivalent of:
                        // - if (IORING_SQ_NEED_WAKEUP) -> io_uring_enter(..., IORING_ENTER_SQ_WAKEUP)
                        // If shim doesn't, replace this call with a shim_submit_wakeup() that does.
                        shim_submit(Ring);
                    }

                    // 3) Wait for at least 1 CQE (1ms timeout), then drain the CQ in a batch.
                    io_uring_cqe* cqe;
                    int rc = shim_wait_cqes(Ring, &cqe, 1u, &ts);

                    if (rc is -62 or < 0) { _counter++; continue; }

                    int got;
                    fixed (io_uring_cqe** pC = cqes) got = shim_peek_batch_cqe(Ring, pC, (uint)Config.BatchCqes);
                    
                    for (int i = 0; i < got; i++) {
                        cqe = cqes[i];

                        ulong ud = shim_cqe_get_data64(cqe);
                        UdKind kind = UdKindOf(ud);
                        int res = cqe->res;

                        if (kind == UdKind.Recv) {
                            int fd = UdFdOf(ud);

                            bool hasBuffer = shim_cqe_has_buffer(cqe) != 0;
                            bool hasMore = (cqe->flags & IORING_CQE_F_MORE) != 0;

                            if (res <= 0) {
                                // Return buffer to ring if kernel provided one.
                                if (hasBuffer) {
                                    ushort bufferId = (ushort)shim_cqe_buffer_id(cqe);
                                    byte* addr = _bufferRingSlab + (nuint)bufferId * (nuint)Config.RecvBufferSize;
                                    shim_buf_ring_add(
                                        _bufferRing,
                                        addr,
                                        (uint)Config.RecvBufferSize,
                                        bufferId,
                                        (ushort)_bufferRingMask,
                                        _bufferRingIndex++);
                                    shim_buf_ring_advance(_bufferRing, 1);
                                }

                                // IMPORTANT: remove from dictionary BEFORE returning to pool / closing fd.
                                // This prevents stale CQEs (already produced) from finding a recycled Connection.
                                if (connections.TryGetValue(fd, out Connection? connection)) {
                                    connections.Remove(fd);

                                    _engine.ConnectionPool.Return(connection);
                                    close(fd);
                                }
                            }else {
                                ushort bufferId = (ushort)shim_cqe_buffer_id(cqe);

                                if (connections.TryGetValue(fd, out Connection? connection)) {
                                    connection.HasBuffer = hasBuffer;
                                    connection.BufferId = bufferId;
                                    connection.InPtr = _bufferRingSlab + (nuint)bufferId * (nuint)Config.RecvBufferSize;
                                    connection.InLength = res;

                                    // Wake consumer
                                    connection.SignalReadReady();

                                    // If multishot stopped (no MORE flag), re-arm.
                                    if (!hasMore) {
                                        ArmRecvMultishot(Ring, fd, c_bufferRingGID);
                                        queuedSqe = true;
                                    }
                                } else {
                                    // Defensive: if we got a recv for an fd we don't track,
                                    // return its buffer so we don't leak ring entries.
                                    if (hasBuffer) {
                                        byte* addr = _bufferRingSlab + (nuint)bufferId * (nuint)Config.RecvBufferSize;
                                        shim_buf_ring_add(
                                            _bufferRing,
                                            addr,
                                            (uint)Config.RecvBufferSize,
                                            bufferId,
                                            (ushort)_bufferRingMask,
                                            _bufferRingIndex++);
                                        shim_buf_ring_advance(_bufferRing, 1);
                                    }
                                }
                            }
                        } else if (kind == UdKind.Send) {
                            int fd = UdFdOf(ud);

                            if (connections.TryGetValue(fd, out Connection? connection)) {
                                if (res <= 0) {
                                    // Treat send errors like close. Remove first to avoid stale use.
                                    connections.Remove(fd);

                                    // If this connection held a buffer ring entry, return it.
                                    // (Only if your send path can happen while HasBuffer is true.)
                                    if (connection.HasBuffer) {
                                        ReturnBufferRing(connection.InPtr, connection.BufferId);
                                        connection.HasBuffer = false;
                                    }

                                    _engine.ConnectionPool.Return(connection);
                                    close(fd);
                                } else {
                                    connection.OutHead += (nuint)res;

                                    if (connection.OutHead < connection.OutTail)
                                    {
                                        SubmitSend(
                                            Ring,
                                            connection.ClientFd,
                                            connection.OutPtr,
                                            connection.OutHead,
                                            connection.OutTail);
                                        queuedSqe = true;
                                    }
                                }
                            }
                        }
                        shim_cqe_seen(Ring, cqe);
                    }

                    // If we queued SQEs while processing CQEs (re-arms / continued sends), submit once.
                    if (queuedSqe || shim_sq_ready(Ring) > 0) {
                        // Same SQPOLL note as above: must wake on NEED_WAKEUP.
                        shim_submit(Ring);
                    }
                }
            } finally {
                // Close any remaining connections
                CloseAll(connections);

                // Free buffer ring BEFORE destroying the ring
                if (Ring != null && _bufferRing != null) {
                    shim_free_buf_ring(Ring, _bufferRing, (uint)Config.BufferRingEntries, c_bufferRingGID);
                    _bufferRing = null;
                }

                // Destroy ring (unregisters CQ/SQ memory mappings)
                if (Ring != null) {
                    shim_destroy_ring(Ring);
                    Ring = null;
                }

                // Free slab memory used by buf ring
                if (_bufferRingSlab != null) {
                    NativeMemory.AlignedFree(_bufferRingSlab);
                    _bufferRingSlab = null;
                }

                Console.WriteLine($"[w{Id}] Shutdown complete. (SQPOLL={isSqPoll})");
            }
        }
        
        public void SubmitSend(io_uring* pring, int fd, byte* buf, nuint off, nuint len) {
            io_uring_sqe* sqe = SqeGet(pring);
            shim_prep_send(sqe, fd, buf + off, (uint)(len - off), 0);
            shim_sqe_set_data64(sqe, PackUd(UdKind.Send, fd));
        }
        
        private void CloseAll(Dictionary<int, Connection> connections) {
            foreach (var connection in connections) {
                try { close(connection.Value.ClientFd); _engine.ConnectionPool.Return(connection.Value); } catch { /* ignore */ }
            }
        }
    }
}