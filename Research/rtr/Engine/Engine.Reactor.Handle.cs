using System.Runtime.InteropServices;
using static rtr.ABI.ABI;

namespace rtr.Engine;

public sealed unsafe partial class Engine {
    public partial class Reactor {
        internal void Handle() {
            Dictionary<int, Connection> connections = _engine.Connections[Id];
            io_uring_cqe*[] cqes = new io_uring_cqe*[Config.BatchCqes];

            try {
                io_uring_cqe* cqe;
                __kernel_timespec ts;
                ts.tv_sec  = 0;
                ts.tv_nsec = Config.CqTimeout;
                int one = 1;
                while (_engine.ServerRunning) {
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
                                if (hasBuffer) {
                                    ushort bufferId = (ushort)shim_cqe_buffer_id(cqe);
                                    byte* addr = _bufferRingSlab + (nuint)bufferId * (nuint)Config.RecvBufferSize;
                                    ReturnBufferRing(addr, bufferId);
                                }
                                if (connections.Remove(fd, out var connection)) {
                                    connection.MarkClosed(res);
                                    _engine.ConnectionPool.Return(connection);
                                    SubmitCancelRecv(io_uring_instance, fd);
                                    close(fd);
                                }
                                continue;
                            }
                            if (!hasBuffer) continue;
                            ushort bid = (ushort)shim_cqe_buffer_id(cqe);
                            if (connections.TryGetValue(fd, out var connection2)) {
                                byte* ptr = _bufferRingSlab + (nuint)bid * (nuint)Config.RecvBufferSize;
                                connection2.EnqueueRingItem(ptr, res, bid);
                                if (!hasMore) {
                                    ArmRecvMultishot(io_uring_instance, fd, c_bufferRingGID);
                                }
                            } else {
                                ReturnBufferRing(_bufferRingSlab + (nuint)bid * (nuint)Config.RecvBufferSize, bid);
                            }
                        } else if (kind == UdKind.Send) {
                            int fd = UdFdOf(ud);
                            if (connections.TryGetValue(fd, out var connection)) {
                                if (res <= 0) {
                                    Volatile.Write(ref connection.SendInflight, 0);
                                    continue;
                                }
                                connection.WriteHead += res;
                                int target = connection.WriteInFlight;
                                if (connection.WriteHead < target) {
                                    SubmitSend(io_uring_instance, fd, connection.WriteBuffer, (uint)connection.WriteHead, (uint)target);
                                    continue;
                                }
                                Volatile.Write(ref connection.SendInflight, 0);
                                connection.WriteInFlight = 0;
                                connection.ResetWriteBuffer();
                                if (connection.IsFlushInProgress) connection.CompleteFlush();
                            }
                        } else if (kind == UdKind.Accept) {
                            if (res >= 0) {
                                int clientFd = res;
                                setsockopt(clientFd, IPPROTO_TCP, TCP_NODELAY, &one, (uint)sizeof(int));

                                Connection conn = _engine.ConnectionPool.Get()
                                    .SetFd(clientFd)
                                    .SetReactor(_engine.Reactors[Id]);
                                connections[clientFd] = conn;

                                ArmRecvMultishot(io_uring_instance, clientFd, c_bufferRingGID);

                                bool added = _engine.ConnectionQueues.Writer.TryWrite(new ConnectionItem(Id, clientFd));
                                if (!added) Console.WriteLine($"[w{Id}] Failed to publish connection");
                            } else {
                                Console.WriteLine($"[w{Id}] Accept error: {res}");
                            }
                        } else if (kind == UdKind.Cancel) { }
                    }
                    shim_cq_advance(io_uring_instance, (uint)got);
                }
            }finally {
                CloseListener();
                CloseAll(connections);
                if (io_uring_instance != null && _bufferRing != null) {
                    DrainReturnQ();
                    shim_free_buf_ring(io_uring_instance, _bufferRing, (uint)Config.BufferRingEntries, c_bufferRingGID);
                    _bufferRing = null;
                }
                if (io_uring_instance != null) {
                    shim_destroy_ring(io_uring_instance);
                    io_uring_instance = null;
                }
                if (_bufferRingSlab != null) {
                    NativeMemory.AlignedFree(_bufferRingSlab);
                    _bufferRingSlab = null;
                }
                Console.WriteLine($"Reactor[{Id}] Shutdown complete.");
            }
        }
    }
}
