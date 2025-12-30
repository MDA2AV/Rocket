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

        private void ReturnBufferRing(byte* addr, ushort bid) {
            shim_buf_ring_add(_bufferRing, addr, (uint)Config.RecvBufferSize, bid, (ushort)_bufferRingMask, _bufferRingIndex++);
            shim_buf_ring_advance(_bufferRing, 1);
        }
        
        private readonly ConcurrentQueue<ushort> _returns = new();
        public void EnqueueReturn(ushort bid) => _returns.Enqueue(bid);
        
        internal void Handle() {
            Dictionary<int,Connection> connections = _engine.Connections[Id];
            ConcurrentQueue<int> reactorQueue = ReactorQueues[Id];     // new FDs from acceptor
            io_uring_cqe*[] cqes = new io_uring_cqe*[Config.BatchCqes];

            try {
                while (_engine.ServerRunning) {
                    // Drain new connections
                    while (reactorQueue.TryDequeue(out int newFd)) { ArmRecvMultishot(Ring, newFd, c_bufferRingGID); }
                    // Drain rings returns
                    while (_returns.TryDequeue(out ushort bid)) {
                        byte* addr = _bufferRingSlab + (nuint)bid * (nuint)Config.RecvBufferSize;
                        shim_buf_ring_add(_bufferRing, addr, (uint)Config.RecvBufferSize, bid, (ushort)_bufferRingMask, _bufferRingIndex++);
                        shim_buf_ring_advance(_bufferRing, 1);
                    }
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
                                Console.WriteLine($"[w{Id}] recv res={res} fd={fd}");
                                // Return the CQE's provided buffer (if any)
                                if (hasBuffer) {
                                    ushort bufferId = (ushort)shim_cqe_buffer_id(cqe);
                                    byte* addr = _bufferRingSlab + (nuint)bufferId * (nuint)Config.RecvBufferSize;
                                    shim_buf_ring_add(_bufferRing, addr, (uint)Config.RecvBufferSize, bufferId, (ushort)_bufferRingMask, _bufferRingIndex++);
                                    shim_buf_ring_advance(_bufferRing, 1);
                                }
                                // REMOVE the connection mapping so we don't process this fd again,
                                // and so fd reuse won't hit a stale Connection.
                                if (connections.Remove(fd, out var connection)) {
                                    // Return any queued buffers that the handler never consumed
                                    while (connection.TryDequeueRecv(out var item)) { EnqueueReturn(item.BufferId); }
                                    _engine.ConnectionPool.Return(connection);
                                    // Close once (only if we owned this connection)
                                    close(fd);
                                } 
                                shim_cqe_seen(Ring, cqe);
                                continue;
                            } else {
                                var bufferId = (ushort)shim_cqe_buffer_id(cqe);
                                var ptr = _bufferRingSlab + (nuint)bufferId * (nuint)Config.RecvBufferSize;
                                
                                if (connections.TryGetValue(fd, out var connection)) {
                                    // With buf_ring, you *should* have hasBuffer=true here; if not, handle separately.
                                    connection.EnqueueRecv(ptr, res, bufferId);
                                    if (!hasMore) ArmRecvMultishot(Ring, fd, c_bufferRingGID);
                                } else { ReturnBufferRing(ptr, bufferId); }
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