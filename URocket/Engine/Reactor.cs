using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.ObjectPool;
using URocket.Engine.Configs;
using URocket.Utils;
using URocket.Utils.MultiProducerSingleConsumer;
using static URocket.ABI.ABI;

// ReSharper disable always CheckNamespace
// ReSharper disable always SuggestVarOrType_BuiltInTypes
// (var is avoided intentionally in this project so that concrete types are visible at call sites.)

namespace URocket.Engine;

public sealed unsafe partial class Engine {
    private readonly ObjectPool<Connection.Connection> ConnectionPool =
        new DefaultObjectPool<Connection.Connection>(new ConnectionPoolPolicy(), 1024 * 32);

    private class ConnectionPoolPolicy : PooledObjectPolicy<Connection.Connection> {
        public override Connection.Connection Create() => new();
        public override bool Return(Connection.Connection connection) { connection.Clear(); return true; }
    }
    
    public class Reactor {
        private int _counter;
        private int _ringCounter;
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
            if (Ring == null || err != 0) {
                Console.WriteLine($"create_ring failed: {err}");
                return; // or throw; but do NOT continue using ring
            }

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
            _ringCounter++;
            shim_buf_ring_add(_bufferRing, addr, (uint)Config.RecvBufferSize, bid, (ushort)_bufferRingMask, _bufferRingIndex++);
            shim_buf_ring_advance(_bufferRing, 1);
        }
        
        private readonly MpscUshortQueue _returnQ = new(1 << 16); // 65536 slots (power-of-two)
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EnqueueReturnQ(ushort bid) {
            if (!_returnQ.TryEnqueue(bid)) {
                SpinWait sw = default;
                while (!_returnQ.TryEnqueue(bid)) {
                    sw.SpinOnce();
                    if (sw.Count > 50) {
                        if (!_engine.ServerRunning) return; // only bail out after trying
                        Thread.Yield();
                        sw.Reset();
                    }
                }
            }
        }

        private void DrainReturnQ() {
            while (_returnQ.TryDequeue(out ushort bid)) {
                byte* addr = _bufferRingSlab + (nuint)bid * (nuint)Config.RecvBufferSize;
                ReturnBufferRing(addr, bid);
            }
        }
        
        private int DrainReturnQCounted() {
            int count = 0;
            while (_returnQ.TryDequeue(out ushort bid)) {
                byte* addr = _bufferRingSlab + (nuint)bid * (nuint)Config.RecvBufferSize;
                ReturnBufferRing(addr, bid); // queues 1 SQE (buf_ring_add + advance)
                count++;
            }
            return count;
        }
        
        private readonly MpscWriteItem _write = new(capacityPow2: 1024);

        public bool TryEnqueueWrite(WriteItem item) {
            return _write.TryEnqueue(item);
        }

        public bool TryDequeueWrite(out WriteItem item) {
            if (_write.TryDequeue(out item))
                return true;

            return false;
        }

        private readonly HashSet<int> _flushableFds = [];
        
        private void DrainWriteQ() {
            //Console.WriteLine("Draining WriteQ");
            _flushableFds.Clear();
            
            while (_write.TryDequeue(out WriteItem item))
            {
                if (_engine.Connections[Id].TryGetValue(item.ClientFd, out var connection))
                {
                    if (connection.Flushable)
                    {
                        // Write buffer and free it
                        connection.Write(item.Buffer.Ptr, item.Buffer.Length);
                        item.Buffer.Free();
                        
                        _flushableFds.Add(connection.ClientFd);
                    }
                }
            }

            foreach (int fd in _flushableFds)
            {
                if (_engine.Connections[Id].TryGetValue(fd, out var connection))
                {
                    connection.Flush();
                    connection.Flushable = false;
                }
            }
        }
        
        internal void HandleSubmitAndWaitSingleCall()
        {
            Dictionary<int, Connection.Connection> connections = _engine.Connections[Id];
            ConcurrentQueue<int> reactorQueue = ReactorQueues[Id];
            io_uring_cqe*[] cqes = new io_uring_cqe*[Config.BatchCqes];

            try {
                io_uring_cqe* cqe;
                while (_engine.ServerRunning) {
                    // Drain new connections
                    while (reactorQueue.TryDequeue(out int newFd)) {
                        connections[newFd] = _engine.ConnectionPool.Get()
                            .SetFd(newFd)
                            .SetReactor(_engine.Reactors[Id]);

                        // Queue multishot recv SQE (will be flushed by submit_and_wait_timeout)
                        ArmRecvMultishot(Ring, newFd, c_bufferRingGID);

                        bool connectionAdded = _engine.ConnectionQueues.Writer.TryWrite(new ConnectionItem(Id, newFd));
                        if (!connectionAdded) Console.WriteLine("Failed to write connection!!");
                    }

                    // Return provided buffers back into the buf_ring (queues SQEs; flushed below)
                    DrainReturnQ();
                    
                    DrainWriteQ();

                    // One call that:
                    //  - flushes queued SQEs (liburing)
                    //  - submits to kernel
                    //  - waits for at least 1 CQE (or timeout)
                    __kernel_timespec ts;
                    ts.tv_sec  = 0;
                    ts.tv_nsec = Config.CqTimeout;

                    int got;
                    fixed (io_uring_cqe** pC = cqes)
                    {
                        got = shim_peek_batch_cqe(Ring, pC, (uint)Config.BatchCqes);
                        if (got == 0)
                        {
                            int rc = shim_submit_and_wait_timeout(Ring, pC, 1u, &ts);
                            
                            // Hmm, I think this condition should be enough for now
                            if (rc < 0) { _counter++; continue; }
                            //if (rc == -62 || rc < 0 && rc != -17) { _counter++; continue; }
                            //if (rc is -62 or < 0) { _counter++; continue; }

                            got = shim_peek_batch_cqe(Ring, pC, (uint)Config.BatchCqes);
                        }
                        //else
                        //{
                        //    // already have at least 1, now grab the full batch
                        //    got = shim_peek_batch_cqe(Ring, pC, (uint)Config.BatchCqes);
                        //}
                    }

                    for (int i = 0; i < got; i++) {
                        cqe = cqes[i];

                        ulong ud = shim_cqe_get_data64(cqe);
                        UdKind kind = UdKindOf(ud);
                        int res = cqe->res;

                        if (kind == UdKind.Recv) {
                            int fd = UdFdOf(ud);
                            bool hasBuffer = shim_cqe_has_buffer(cqe) != 0;
                            bool hasMore   = (cqe->flags & IORING_CQE_F_MORE) != 0;

                            if (res <= 0) {
                                Console.WriteLine($"[w{Id}] recv res={res} fd={fd}");

                                if (hasBuffer) {
                                    ushort bufferId = (ushort)shim_cqe_buffer_id(cqe);
                                    byte* addr = _bufferRingSlab + (nuint)bufferId * (nuint)Config.RecvBufferSize;
                                    ReturnBufferRing(addr, bufferId); // queues SQE (will flush on next loop)
                                }

                                if (connections.Remove(fd, out var connection)) {
                                    connection.MarkClosed(res);
                                    _engine.ConnectionPool.Return(connection);

                                    // Queue cancel (DO NOT submit here; submit_and_wait_timeout will flush next loop)
                                    SubmitCancelRecv(Ring, fd);

                                    close(fd);
                                }

                                //shim_cqe_seen(Ring, cqe);
                                continue;
                            }
                            
                            // res > 0
                            if (!hasBuffer) {
                                //shim_cqe_seen(Ring, cqe);
                                continue;
                            }

                            ushort bid = (ushort)shim_cqe_buffer_id(cqe);
                            byte* ptr = _bufferRingSlab + (nuint)bid * (nuint)Config.RecvBufferSize;

                            if (connections.TryGetValue(fd, out var connection2)) {
                                connection2.EnqueueRecv(ptr, res, bid);

                                if (!hasMore) {
                                    // Re-arm multishot recv if kernel stopped it
                                    ArmRecvMultishot(Ring, fd, c_bufferRingGID); // queues SQE (flush next loop)
                                }
                            } else {
                                // No connection mapping => immediately return buffer
                                ReturnBufferRing(ptr, bid); // queues SQE (flush next loop)
                            }
                        } else if (kind == UdKind.Send) {
                            int fd = UdFdOf(ud);
                            if (connections.TryGetValue(fd, out var connection)) {
                                connection.WriteHead += res;
                                
                                if (connection.WriteHead < connection.WriteTail) {
                                    Console.WriteLine("Oddness");
                                    SubmitSend(Ring, connection.ClientFd, connection.WriteBuffer, (uint)connection.WriteHead, (uint)connection.WriteTail);
                                    continue;
                                    // queued SQE; flushed next loop
                                }
                                
                                connection.ResetWriteBuffer();
                                //connection.ResetRead();
                            }
                        } else if (kind == UdKind.Cancel) {
                            Console.WriteLine("Cancel");
                            // ignore; res==0 means cancel succeeded, res<0 often means already gone
                        }
                        //shim_cqe_seen(Ring, cqe);
                    }
                    shim_cq_advance(Ring, (uint)got);
                }
            }
            finally
            {
                // Close any remaining connections
                CloseAll(connections);

                // Free buffer ring BEFORE destroying the ring
                if (Ring != null && _bufferRing != null)
                {
                    DrainReturnQ();
                    shim_free_buf_ring(Ring, _bufferRing, (uint)Config.BufferRingEntries, c_bufferRingGID);
                    _bufferRing = null;
                }

                // Destroy ring
                if (Ring != null) { shim_destroy_ring(Ring); Ring = null; }

                // Free slab memory used by buf ring
                if (_bufferRingSlab != null) { NativeMemory.AlignedFree(_bufferRingSlab); _bufferRingSlab = null; }

                Console.WriteLine($"Reactor[{Id}] Shutdown complete.");
            }
        }
        
        /*
        internal void HandleSubmitAndWaitCqe() {
            Dictionary<int,Connection.Connection> connections = _engine.Connections[Id];
            ConcurrentQueue<int> reactorQueue = ReactorQueues[Id];     // new FDs from acceptor
            io_uring_cqe*[] cqes = new io_uring_cqe*[Config.BatchCqes];

            try {
                while (_engine.ServerRunning) {
                    // Drain new connections
                    while (reactorQueue.TryDequeue(out int newFd)) {
                        connections[newFd] = _engine.ConnectionPool.Get()
                            .SetFd(newFd)
                            .SetReactor(_engine.Reactors[Id]);
                        
                        ArmRecvMultishot(Ring, newFd, c_bufferRingGID); 
                        bool connectionAdded = _engine.ConnectionQueues.Writer.TryWrite(new ConnectionItem(Id, newFd));
                        if (!connectionAdded) Console.WriteLine("Failed to write connection!!");
                    }
                    
                    DrainReturnQ(); // Drain rings returns
                    
                    if (shim_sq_ready(Ring) > 0) shim_submit(Ring);
                    io_uring_cqe* cqe; __kernel_timespec ts; ts.tv_sec  = 0; ts.tv_nsec = Config.CqTimeout; // 1 ms timeout
                    int rc = shim_wait_cqes(Ring, &cqe, (uint)1, &ts); int got;
                    
                    // Hmm, I think this condition should be enough for now
                    if (rc < 0) { _counter++; continue; }
                    //if (rc == -62 || rc < 0 && rc != -17) { _counter++; continue; }
                    //if (rc is -62 or < 0) { _counter++; continue; }
                    
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
                                    ReturnBufferRing(addr, bufferId);
                                }
                                // REMOVE the connection mapping so we don't process this fd again,
                                // and so fd reuse won't hit a stale Connection.
                                if (connections.Remove(fd, out var connection)) {
                                    connection.MarkClosed(res);
                                    _engine.ConnectionPool.Return(connection);
                                    SubmitCancelRecv(Ring, fd);   // Cancel the multishot recv
                                    if (shim_sq_ready(Ring) > 0) shim_submit(Ring);
                                    // Close once (only if we owned this connection)
                                    close(fd);
                                } 
                                shim_cqe_seen(Ring, cqe);
                                continue;
                            } else {

                                // This should never happen?
                                if (!hasBuffer) {
                                    shim_cqe_seen(Ring, cqe);
                                    continue;
                                }
                                
                                var bufferId = (ushort)shim_cqe_buffer_id(cqe);
                                var ptr = _bufferRingSlab + (nuint)bufferId * (nuint)Config.RecvBufferSize;
                                
                                if (connections.TryGetValue(fd, out var connection)) {
                                    // With buf_ring, you *should* have hasBuffer=true here; if not, handle separately.
                                    connection.EnqueueRecv(ptr, res, bufferId);
                                    if (!hasMore) ArmRecvMultishot(Ring, fd, c_bufferRingGID);
                                }else { ReturnBufferRing(ptr, bufferId); } // Immediately return, no need to enqueue
                            }
                        }
                        else if (kind == UdKind.Send) {
                            int fd = UdFdOf(ud);
                            if (connections.TryGetValue(fd, out var connection)) {
                                // Advance send progress.
                                connection.WriteHead += res;
                                if (connection.WriteHead < connection.WriteTail)
                                    SubmitSend(Ring, connection.ClientFd, connection.WriteBuffer, (uint)connection.WriteHead, (uint)connection.WriteTail);
                            }
                        }
                        else if (kind == UdKind.Cancel) {
                            Console.WriteLine("Cancel");
                            // ignore; res==0 means cancel succeeded, res<0 often means it was already gone
                        }
                        shim_cqe_seen(Ring, cqe);
                    }
                }
            } finally {
                // Close any remaining connections
                CloseAll(connections);
                // Free buffer ring BEFORE destroying the ring
                if (Ring != null && _bufferRing != null) {
                    DrainReturnQ();
                    shim_free_buf_ring(Ring, _bufferRing, (uint)Config.BufferRingEntries, c_bufferRingGID);
                    _bufferRing = null;
                }
                // Destroy ring (unregisters CQ/SQ memory mappings)
                if (Ring != null) { shim_destroy_ring(Ring); Ring = null; }
                // Free slab memory used by buf ring
                if (_bufferRingSlab != null) { NativeMemory.AlignedFree(_bufferRingSlab); _bufferRingSlab = null; }
                Console.WriteLine($"Reactor[{Id}] Shutdown complete.");
            }
        }
        */

        public void Send(int clientFd, byte* buf, nuint off, nuint len) {
            io_uring_sqe* sqe = SqeGet(Ring);
            shim_prep_send(sqe, clientFd, buf + off, (uint)(len - off), 0);
            shim_sqe_set_data64(sqe, PackUd(UdKind.Send, clientFd)); 
        }
        
        public static void SubmitSend(io_uring* pring, int fd, byte* buf, nuint off, nuint len) {
            io_uring_sqe* sqe = SqeGet(pring);
            shim_prep_send(sqe, fd, buf + off, (uint)(len - off), 0);
            shim_sqe_set_data64(sqe, PackUd(UdKind.Send, fd));
        }
        
        private static void SubmitCancelRecv(io_uring* ring, int fd) {
            io_uring_sqe* sqe = shim_get_sqe(ring);
            if (sqe == null) return; // or handle SQ full

            ulong target = PackUd(UdKind.Recv, fd);

            shim_prep_cancel64(sqe, target, /*flags*/ 0 /* or IORING_ASYNC_CANCEL_ALL */);
            shim_sqe_set_data64(sqe, PackUd(UdKind.Cancel, fd));
        }
        
        private void CloseAll(Dictionary<int, Connection.Connection> connections) {
            Console.WriteLine($"Reactor[{Id}] Connection leakage -- [{connections.Count}] " +
                              $"Ring leakage -- [{_ringCounter + Config.BufferRingEntries - _bufferRingIndex}]");
            
            foreach (var kv in connections) {
                var conn = kv.Value;

                // Mark closed to wake any waiter.
                conn.MarkClosed(error: 0);

                // Remove mapping first (so late CQEs won't find it).
                // Already doing remove on res<=0 path; doing same here if needed.

                // Close fd
                try { close(conn.ClientFd); } catch { /* ignore */ }

                // Pool it. Safe only because:
                //   - ReadAsync uses generation/closed => will return Closed for stale handlers
                //   - We did NOT return any recv buffers here
                _engine.ConnectionPool.Return(conn);
            }

            connections.Clear();
        }
    }
}