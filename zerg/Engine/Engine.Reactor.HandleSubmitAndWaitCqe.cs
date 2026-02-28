using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using static zerg.ABI.ABI;

namespace zerg.Engine;

public sealed unsafe partial class Engine
{
    public partial class Reactor
    {
        internal void HandleSubmitAndWaitCqe()
        {
            Dictionary<int,Connection> connections = _engine.Connections[Id];
            ConcurrentQueue<int> reactorQueue = ReactorQueues[Id];     // new FDs from acceptor
            io_uring_cqe*[] cqes = new io_uring_cqe*[Config.BatchCqes];

            try
            {
                io_uring_cqe* cqe;
                __kernel_timespec ts;
                ts.tv_sec  = 0;
                ts.tv_nsec = Config.CqTimeout; // 1 ms timeout

                while (_engine.ServerRunning)
                {
                    // Drain new connections
                    while (reactorQueue.TryDequeue(out int newFd))
                    {
                        Connection conn = _engine.ConnectionPool.Get()
                            .SetFd(newFd)
                            .SetReactor(_engine.Reactors[Id]);
                        connections[newFd] = conn;

                        if (_incrementalMode)
                        {
                            SetupConnectionBufRing(conn);
                            ArmRecvMultishot(io_uring_instance, newFd, conn.Bgid);
                        }
                        else
                        {
                            ArmRecvMultishot(io_uring_instance, newFd, c_bufferRingGID);
                        }

                        bool connectionAdded = _engine.ConnectionQueues.Writer.TryWrite(new ConnectionItem(Id, newFd));
                        if (!connectionAdded) Console.WriteLine("Failed to write connection!!");
                    }

                    DrainReturnQ(); // Drain rings returns
                    if (_incrementalMode) DrainReturnQIncremental();

                    DrainFlushQ();

                    if (shim_sq_ready(io_uring_instance) > 0)
                        shim_submit(io_uring_instance);

                    int rc = shim_wait_cqes(io_uring_instance, &cqe, (uint)1, &ts); int got;

                    if (rc < 0)
                    {
                        continue;
                    }

                    fixed (io_uring_cqe** pC = cqes)
                        got = shim_peek_batch_cqe(io_uring_instance, pC, (uint)Config.BatchCqes);

                    for (int i = 0; i < got; i++)
                    {
                        cqe = cqes[i];
                        ulong ud = shim_cqe_get_data64(cqe);
                        UdKind kind = UdKindOf(ud);
                        int res  = cqe->res;

                        if (kind == UdKind.Recv)
                        {
                            int fd = UdFdOf(ud);
                            bool hasBuffer = shim_cqe_has_buffer(cqe) != 0;
                            bool hasMore   = (cqe->flags & IORING_CQE_F_MORE) != 0;

                            if (res <= 0)
                            {
                                Console.WriteLine($"[w{Id}] recv res={res} fd={fd}");
                                // Return the CQE's provided buffer (if any)
                                if (hasBuffer)
                                {
                                    ushort bufferId = (ushort)shim_cqe_buffer_id(cqe);
                                    if (connections.TryGetValue(fd, out var connClose) && connClose.IncrementalMode)
                                    {
                                        connClose.BufKernelDone![bufferId] = true;
                                        if (connClose.BufRefCounts![bufferId] <= 0)
                                            ReturnConnectionBuffer(connClose, bufferId);
                                    }
                                    else if (!_incrementalMode)
                                    {
                                        byte* addr = _bufferRingSlab + (nuint)bufferId * (nuint)Config.RecvBufferSize;
                                        ReturnBufferRing(addr, bufferId);
                                    }
                                }
                                // REMOVE the connection mapping so we don't process this fd again,
                                // and so fd reuse won't hit a stale Connection.
                                if (connections.Remove(fd, out var connection))
                                {
                                    connection.MarkClosed(res);
                                    if (connection.IncrementalMode)
                                        TeardownConnectionBufRing(connection);
                                    _engine.ConnectionPool.Return(connection);
                                    SubmitCancelRecv(io_uring_instance, fd);   // Cancel the multishot recv
                                    if (shim_sq_ready(io_uring_instance) > 0)
                                        shim_submit(io_uring_instance);
                                    // Close once (only if we owned this connection)
                                    close(fd);
                                }
                                shim_cqe_seen(io_uring_instance, cqe);
                                continue;
                            }
                            else
                            {

                                // This should never happen?
                                if (!hasBuffer)
                                {
                                    shim_cqe_seen(io_uring_instance, cqe);
                                    continue;
                                }

                                var bufferId = (ushort)shim_cqe_buffer_id(cqe);

                                if (connections.TryGetValue(fd, out var connection))
                                {
                                    byte* ptr;
                                    if (connection.IncrementalMode)
                                    {
                                        ptr = connection.BufRingSlab + (nuint)bufferId * (nuint)Config.RecvBufferSize
                                              + (nuint)connection.BufCumulativeOffset![bufferId];
                                        connection.BufCumulativeOffset[bufferId] += res;
                                        bool bufMore = (cqe->flags & IORING_CQE_F_BUF_MORE) != 0;
                                        connection.BufRefCounts![bufferId]++;
                                        if (!bufMore) connection.BufKernelDone![bufferId] = true;
                                    }
                                    else
                                    {
                                        ptr = _bufferRingSlab + (nuint)bufferId * (nuint)Config.RecvBufferSize;
                                    }

                                    connection.EnqueueRingItem(ptr, res, bufferId);
                                    if (!hasMore)
                                        ArmRecvMultishot(io_uring_instance, fd,
                                            connection.IncrementalMode ? (uint)connection.Bgid : c_bufferRingGID);
                                }
                                else
                                {
                                    if (!_incrementalMode)
                                        ReturnBufferRing(_bufferRingSlab + (nuint)bufferId * (nuint)Config.RecvBufferSize, bufferId);
                                }
                            }
                        }
                        else if (kind == UdKind.Send)
                        {
                            int fd = UdFdOf(ud);
                            if (connections.TryGetValue(fd, out var connection))
                            {
                                if (res <= 0)
                                {
                                    // error/close handling
                                    Volatile.Write(ref connection.SendInflight, 0);
                                    continue;
                                }

                                connection.WriteHead += res;

                                int target = connection.WriteInFlight;

                                // Still flushing target snapshot
                                if (connection.WriteHead < target)
                                {
                                    // Correct: len is total-end (target), not remaining
                                    SubmitSend(io_uring_instance, fd, connection.WriteBuffer, (uint)connection.WriteHead, (uint)target);
                                    continue;
                                }

                                // Flush batch done
                                Volatile.Write(ref connection.SendInflight, 0);

                                connection.WriteInFlight = 0;
                                connection.ResetWriteBuffer(); // safe because _flushInProgress forbids concurrent writes

                                if (connection.IsFlushInProgress)
                                    connection.CompleteFlush();
                            }
                        }
                        else if (kind == UdKind.Cancel)
                        {
                            Console.WriteLine("Cancel");
                            // ignore; res==0 means cancel succeeded, res<0 often means it was already gone
                        }
                        shim_cqe_seen(io_uring_instance, cqe);
                    }
                }
            }
            finally
            {
                // Close any remaining connections
                CloseAll(connections);
                // Free buffer ring BEFORE destroying the ring
                if (io_uring_instance != null && _bufferRing != null)
                {
                    DrainReturnQ();
                    shim_free_buf_ring(io_uring_instance, _bufferRing, (uint)Config.BufferRingEntries, c_bufferRingGID);
                    _bufferRing = null;
                }
                // Destroy ring (unregisters CQ/SQ memory mappings)
                if (io_uring_instance != null)
                {
                    shim_destroy_ring(io_uring_instance); io_uring_instance = null;
                }
                // Free slab memory used by buf ring
                if (_bufferRingSlab != null)
                {
                    NativeMemory.AlignedFree(_bufferRingSlab); _bufferRingSlab = null;
                }
                Console.WriteLine($"Reactor[{Id}] Shutdown complete.");
            }
        }
    }
}
