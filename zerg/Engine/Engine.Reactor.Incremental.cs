using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using zerg.Utils.MultiProducerSingleConsumer;
using static zerg.ABI.ABI;

namespace zerg.Engine;

public sealed unsafe partial class Engine
{
    public partial class Reactor
    {
        // Per-connection buffer ring state (incremental mode)
        private Stack<ushort>? _freeGids;
        private MpscUlongQueue? _returnQInc;

        private void InitRingIncremental()
        {
            // Per-connection rings, no shared ring.
            // GID 1 is reserved (shared ring slot, unused). Per-connection GIDs start at 2.
            _freeGids = new Stack<ushort>(Config.MaxConnectionsPerReactor);
            for (int g = Config.MaxConnectionsPerReactor + 1; g >= 2; g--)
                _freeGids.Push((ushort)g);

            _returnQInc = new MpscUlongQueue(1 << 16);
        }
        
        // Per-connection buffer ring lifecycle
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
            shim_buf_ring_add(conn.BufRing, addr, (uint)Config.RecvBufferSize, bid, (ushort)conn.BufRingMask, 0);
            shim_buf_ring_advance(conn.BufRing, 1);
        }
        
        // Incremental return queue (handler → reactor)
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

                conn.BufRefCounts![bid]--;
                if (conn.BufRefCounts[bid] <= 0 && conn.BufKernelDone![bid])
                {
                    ReturnConnectionBuffer(conn, bid);
                }
            }
        }
    }
}
