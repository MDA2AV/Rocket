using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.ObjectPool;
using static Rocket.ABI;

// ReSharper disable always CheckNamespace
// ReSharper disable always SuggestVarOrType_BuiltInTypes
// (var is avoided intentionally in this project so that concrete types are visible at call sites.)

namespace Rocket.Engine;

public sealed unsafe partial class RocketEngine {
    private static readonly ObjectPool<Connection> ConnectionPool =
        new DefaultObjectPool<Connection>(new ConnectionPoolPolicy(), 1024 * 32);

    private class ConnectionPoolPolicy : PooledObjectPolicy<Connection> {
        public override Connection Create() => new();
        public override bool Return(Connection connection) { connection.Clear(); return true; }
    }
    
    public static Worker[] s_Workers = null!;
    public static Dictionary<int, Connection>[] Connections = null!;
    
    public class Worker
    {
        public int Counter = 0;
        
        public Worker(int workerIndex) { _workerIndex = workerIndex; }
        
        internal readonly int _workerIndex;

        public io_uring* PRing;
        internal io_uring_buf_ring* BufferRing;
        internal byte* BufferRingSlab;
        internal uint BufferRingIndex = 0;
        internal uint BufferRingMask;

        internal void InitPRing()
        {
            PRing = shim_create_ring((uint)s_ringEntries, out var err);
            uint ringFlags = shim_get_ring_flags(PRing);
            if (PRing == null || err < 0) { Console.Error.WriteLine($"[w{_workerIndex}] create_ring failed: {err}"); return; }
            
            // Setup buffer ring
            // TODO: Investigate this c_bufferRingGID
            BufferRing = shim_setup_buf_ring(PRing, (uint)s_bufferRingEntries, c_bufferRingGID, 0, out var ret);
            if (BufferRing == null || ret < 0) throw new Exception($"setup_buf_ring failed: ret={ret}");

            BufferRingMask = (uint)(s_bufferRingEntries - 1);
            nuint slabSize = (nuint)(s_bufferRingEntries * s_recvBufferSize);
            BufferRingSlab = (byte*)NativeMemory.AlignedAlloc(slabSize, 64);

            for (ushort bid = 0; bid < s_bufferRingEntries; bid++) {
                byte* addr = BufferRingSlab + (nuint)bid * (nuint)s_recvBufferSize;
                shim_buf_ring_add(BufferRing, addr, (uint)s_recvBufferSize, bid, (ushort)BufferRingMask, BufferRingIndex++);
            }
            shim_buf_ring_advance(BufferRing, (uint)s_bufferRingEntries);
        }

        public void ReturnBufferRing(byte* addr, ushort bid) {
            shim_buf_ring_add(BufferRing, addr, (uint)s_recvBufferSize, bid, (ushort)BufferRingMask, BufferRingIndex++);
            shim_buf_ring_advance(BufferRing, 1);
        }
    }
    
    private static unsafe void WorkerLoop(int workerIndex) {
        Dictionary<int,Connection> connections = Connections[workerIndex];
        
        Worker worker   = s_Workers[workerIndex];
        ConcurrentQueue<int> myQueue = WorkerQueues[workerIndex];     // new FDs from acceptor
        io_uring_cqe*[] cqes = new io_uring_cqe*[s_batchCQES];
        const long WaitTimeoutNs = 1_000_000; // 1 ms

        try {
            while (!StopAll) {
                while (myQueue.TryDequeue(out int newFd)) { ArmRecvMultishot(worker.PRing, newFd, c_bufferRingGID); }
                if (shim_sq_ready(worker.PRing) > 0) shim_submit(worker.PRing);
                io_uring_cqe* cqe; __kernel_timespec ts; ts.tv_sec  = 0; ts.tv_nsec = WaitTimeoutNs; // 1 ms timeout
                int rc = shim_wait_cqes(worker.PRing, &cqe, (uint)1, &ts); int got;
                
                if (rc == -62) { worker.Counter++; continue; }
                if (rc < 0) { worker.Counter++; continue; }
                fixed (io_uring_cqe** pC = cqes) got = shim_peek_batch_cqe(worker.PRing, pC, (uint)s_batchCQES);

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
                            Console.WriteLine($"{worker._workerIndex} {worker.Counter}");
                            if (hasBuffer) {
                                ushort bufferId = (ushort)shim_cqe_buffer_id(cqe);
                                byte* addr = worker.BufferRingSlab + (nuint)bufferId * (nuint)s_recvBufferSize;
                                shim_buf_ring_add(worker.BufferRing, addr, (uint)s_recvBufferSize, bufferId, (ushort)worker.BufferRingMask, worker.BufferRingIndex++);
                                shim_buf_ring_advance(worker.BufferRing, 1);
                            }
                            if (connections.TryGetValue(fd, out var connection)) {
                                ConnectionPool.Return(connection);
                                close(fd);
                            }
                        } else {
                            var bufferId = (ushort)shim_cqe_buffer_id(cqe);

                            if (connections.TryGetValue(fd, out var connection)) {
                                connection.HasBuffer = hasBuffer;
                                connection.BufferId = bufferId;
                                connection.InPtr = worker.BufferRingSlab + (nuint)connection.BufferId * (nuint)s_recvBufferSize;
                                connection.InLength = res;
                                connection.SignalReadReady();
                                
                                if (!hasMore) ArmRecvMultishot(worker.PRing, fd, c_bufferRingGID);
                            }
                        }
                    }
                    else if (kind == UdKind.Send) {
                        int fd = UdFdOf(ud);
                        if (connections.TryGetValue(fd, out var connection)) {
                            // Advance send progress.
                            connection.OutHead += (nuint)res;
                            if (connection.OutHead < connection.OutTail)
                                SubmitSend(worker.PRing, connection.Fd, connection.OutPtr, connection.OutHead, connection.OutTail);
                        }
                    }
                    shim_cqe_seen(worker.PRing, cqe);
                }
            }
        }
        finally
        {
            // Close any remaining connections
            CloseAll(connections);
            // Free buffer ring BEFORE destroying the ring
            if (worker.PRing != null && worker.BufferRing != null) {
                shim_free_buf_ring(worker.PRing, worker.BufferRing, (uint)s_bufferRingEntries, c_bufferRingGID);
                worker.BufferRing = null;
            }
            // Destroy ring (unregisters CQ/SQ memory mappings)
            if (worker.PRing != null) { shim_destroy_ring(worker.PRing); worker.PRing = null; }
            // Free slab memory used by buf ring
            if (worker.BufferRingSlab != null) { NativeMemory.AlignedFree(worker.BufferRingSlab); worker.BufferRingSlab = null; }
            Console.WriteLine($"[w{workerIndex}] Shutdown complete.");
        }
    }
    
    private static void CloseAll(Dictionary<int, Connection> connections) {
        foreach (var connection in connections) {
            try { close(connection.Value.Fd); ConnectionPool.Return(connection.Value); } catch { /* ignore */ }
        }
    }
}