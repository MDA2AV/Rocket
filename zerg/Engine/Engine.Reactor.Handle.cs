using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using static zerg.ABI.ABI;

namespace zerg.Engine;

public sealed unsafe partial class Engine {
    public partial class Reactor {
        internal void Handle() {
            Dictionary<int, Connection> connections = _engine.Connections[Id];
            ConcurrentQueue<int> reactorQueue = ReactorQueues[Id];
            io_uring_cqe*[] cqes = new io_uring_cqe*[Config.BatchCqes];
            
            try {
                io_uring_cqe* cqe;
                __kernel_timespec ts;
                ts.tv_sec  = 0;
                ts.tv_nsec = Config.CqTimeout;
                while (_engine.ServerRunning) {
                    while (reactorQueue.TryDequeue(out int newFd)) {
                        connections[newFd] = _engine.ConnectionPool.Get()
                            .SetFd(newFd)
                            .SetReactor(_engine.Reactors[Id]);
                        // Queue multishot recv SQE (will be flushed by submit_and_wait_timeout)
                        ArmRecvMultishot(io_uring_instance, newFd, c_bufferRingGID);
                        bool connectionAdded = _engine.ConnectionQueues.Writer.TryWrite(new ConnectionItem(Id, newFd));
                        if (!connectionAdded) Console.WriteLine("Failed to write connection!!");
                    }
                    DrainReturnQ();
                    DrainFlushQ();
                    int got;
                    fixed (io_uring_cqe** pC = cqes) {
                        got = shim_peek_batch_cqe(io_uring_instance, pC, (uint)Config.BatchCqes);
                        if (got == 0) {
                            int rc = shim_submit_and_wait_timeout(io_uring_instance, pC, 1u, &ts);
                            if (rc < 0) continue;
                            got = shim_peek_batch_cqe(io_uring_instance, pC, (uint)Config.BatchCqes);
                        }
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
                                    SubmitCancelRecv(io_uring_instance, fd); // Queue cancel (DO NOT submit here; submit_and_wait_timeout will flush next loop)
                                    close(fd);
                                }
                                continue;
                            }
                            if (!hasBuffer) continue;
                            ushort bid = (ushort)shim_cqe_buffer_id(cqe);
                            byte* ptr = _bufferRingSlab + (nuint)bid * (nuint)Config.RecvBufferSize;
                            if (connections.TryGetValue(fd, out var connection2)) {
                                connection2.EnqueueRingItem(ptr, res, bid);
                                if (!hasMore) {
                                    ArmRecvMultishot(io_uring_instance, fd, c_bufferRingGID); // queues SQE (flush next loop)
                                }
                            } else {
                                ReturnBufferRing(ptr, bid); // No connection mapping => immediately return buffer -> queues SQE (flush next loop)
                            }
                        } else if (kind == UdKind.Send) {
                            int fd = UdFdOf(ud);
                            if (connections.TryGetValue(fd, out var connection)) {
                                if (res <= 0) {
                                    // error/close handling
                                    Volatile.Write(ref connection.SendInflight, 0);
                                    continue;
                                }
                                connection.WriteHead += res;
                                int target = connection.WriteInFlight;
                                // Still flushing target snapshot
                                if (connection.WriteHead < target) {
                                    // Correct: len is total-end (target), not remaining
                                    SubmitSend(io_uring_instance, fd, connection.WriteBuffer, (uint)connection.WriteHead, (uint)target);
                                    continue;
                                }
                                Volatile.Write(ref connection.SendInflight, 0); // Flush batch done
                                connection.WriteInFlight = 0;
                                connection.ResetWriteBuffer(); // safe because _flushInProgress forbids concurrent writes
                                if (connection.IsFlushInProgress) connection.CompleteFlush();
                            }
                        } else if (kind == UdKind.Cancel) { }
                    }
                    shim_cq_advance(io_uring_instance, (uint)got);
                }
            }finally {
                // Close any remaining connections
                CloseAll(connections);
                // Free buffer ring BEFORE destroying the ring
                if (io_uring_instance != null && _bufferRing != null) {
                    DrainReturnQ();
                    shim_free_buf_ring(io_uring_instance, _bufferRing, (uint)Config.BufferRingEntries, c_bufferRingGID);
                    _bufferRing = null;
                }
                // Destroy ring
                if (io_uring_instance != null) {
                    shim_destroy_ring(io_uring_instance); 
                    io_uring_instance = null; 
                }
                // Free slab memory used by buf ring
                if (_bufferRingSlab != null) {
                    NativeMemory.AlignedFree(_bufferRingSlab); 
                    _bufferRingSlab = null; 
                }
                Console.WriteLine($"Reactor[{Id}] Shutdown complete.");
            }
        }
    }
}