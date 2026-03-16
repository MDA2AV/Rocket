using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using terraform.Ring;
using Zerg.Core;
using static terraform.ABI.Libc;
using static terraform.Ring.Constants;

namespace terraform.Engine;

public sealed unsafe partial class Engine
{
    public partial class Reactor
    {
        internal void Handle()
        {
            Dictionary<int, Connection> connections = _engine.Connections[Id];
            ConcurrentQueue<int> reactorQueue = ReactorQueues[Id];
            IoUringCqe*[] cqes = new IoUringCqe*[Config.BatchCqes];

            try
            {
                IoUringCqe* cqe;
                KernelTimespec ts;
                ts.tv_sec  = 0;
                ts.tv_nsec = Config.CqTimeout;

                while (_engine.ServerRunning)
                {
                    while (reactorQueue.TryDequeue(out int newFd))
                    {
                        Connection conn = (Connection)_engine.ConnectionPool.Get()
                            .SetFd(newFd)
                            .SetReactor(_engine.Reactors[Id]);
                        connections[newFd] = conn;

                        ArmRecvMultishot(Ring, newFd, c_bufferRingGID);

                        bool connectionAdded = _engine.ConnectionQueues.Writer.TryWrite(new ConnectionItem(Id, newFd));
                        if (!connectionAdded) Console.WriteLine("Failed to write connection!!");
                    }

                    DrainReturnQ();
                    DrainFlushQ();

                    int got;
                    fixed (IoUringCqe** pC = cqes)
                    {
                        got = Ring.PeekBatchCqe(pC, Config.BatchCqes);
                        if (got == 0)
                        {
                            Ring.SubmitAndWaitTimeout(1u, &ts);
                            got = Ring.PeekBatchCqe(pC, Config.BatchCqes);
                            if (got <= 0) continue;
                        }
                    }

                    for (int i = 0; i < got; i++)
                    {
                        cqe = cqes[i];
                        ulong ud = cqe->user_data;
                        UdKind kind = UdKindOf(ud);
                        int res = cqe->res;

                        if (kind == UdKind.Recv)
                        {
                            int fd = UdFdOf(ud);
                            bool hasBuffer = (cqe->flags & IORING_CQE_F_BUFFER) != 0;
                            bool hasMore   = (cqe->flags & IORING_CQE_F_MORE) != 0;

                            if (res <= 0)
                            {
                                if (hasBuffer)
                                {
                                    ushort bufferId = (ushort)(cqe->flags >> IORING_CQE_BUFFER_SHIFT);
                                    byte* addr = _bufferRingSlab + (nuint)bufferId * (nuint)Config.RecvBufferSize;
                                    ReturnBufferRing(addr, bufferId);
                                }

                                if (connections.Remove(fd, out var connection))
                                {
                                    connection.MarkClosed(res);
                                    _engine.ConnectionPool.Return(connection);
                                    SubmitCancelRecv(fd);
                                    close(fd);
                                }
                                continue;
                            }

                            if (!hasBuffer) continue;

                            ushort bid = (ushort)(cqe->flags >> IORING_CQE_BUFFER_SHIFT);

                            if (connections.TryGetValue(fd, out var connection2))
                            {
                                byte* ptr = _bufferRingSlab + (nuint)bid * (nuint)Config.RecvBufferSize;
                                connection2.EnqueueRingItem(ptr, res, bid);

                                if (!hasMore)
                                {
                                    ArmRecvMultishot(Ring, fd, c_bufferRingGID);
                                }
                            }
                            else
                            {
                                ReturnBufferRing(_bufferRingSlab + (nuint)bid * (nuint)Config.RecvBufferSize, bid);
                            }
                        }
                        else if (kind == UdKind.Send)
                        {
                            int fd = UdFdOf(ud);
                            if (connections.TryGetValue(fd, out var connection))
                            {
                                if (res <= 0)
                                {
                                    Volatile.Write(ref connection.SendInflight, 0);
                                    continue;
                                }

                                connection.WriteHead += res;
                                int target = connection.WriteInFlight;

                                if (connection.WriteHead < target)
                                {
                                    SubmitSend(fd, connection.WriteBuffer, (uint)connection.WriteHead, (uint)target);
                                    continue;
                                }

                                Volatile.Write(ref connection.SendInflight, 0);
                                connection.WriteInFlight = 0;
                                connection.ResetWriteBuffer();
                                if (connection.IsFlushInProgress) connection.CompleteFlush();
                            }
                        }
                        else if (kind == UdKind.Cancel) { }
                    }

                    Ring.CqAdvance((uint)got);
                }
            }
            finally
            {
                CloseAll(connections);

                if (Ring != null && _bufferRing != null)
                {
                    DrainReturnQ();
                    BufRing.Free(Ring, _bufferRing, (uint)Config.BufferRingEntries, c_bufferRingGID);
                    _bufferRing = null;
                }

                Ring?.Dispose();
                Ring = null!;

                if (_bufferRingSlab != null)
                {
                    NativeMemory.AlignedFree(_bufferRingSlab);
                    _bufferRingSlab = null;
                }

                Console.WriteLine($"Reactor[{Id}] Shutdown complete.");
            }
        }
    }
}
