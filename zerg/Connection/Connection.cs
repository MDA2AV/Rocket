using System.Runtime.InteropServices;
using zerg.core;
using static zerg.ABI.ABI;

namespace zerg;

public sealed unsafe partial class Connection : ConnectionBase
{
    // =========================================================================
    // Per-connection buffer ring (incremental mode only)
    // =========================================================================

    internal io_uring_buf_ring* BufRing;
    internal byte* BufRingSlab;
    internal int BufRingEntries;
    internal uint BufRingMask;
    internal uint BufRingIndex;
    internal ushort Bgid;
    internal bool IncrementalMode;
    internal int[]? BufRefCounts;
    internal bool[]? BufKernelDone;
    internal int[]? BufCumulativeOffset;

    // =========================================================================
    // Constructor
    // =========================================================================

    public Connection(int writeSlabSize = 1024 * 16) : base(writeSlabSize) { }

    // =========================================================================
    // Fluent setters (shadow base to return Connection)
    // =========================================================================

    public new Connection SetFd(int fd)
    {
        base.SetFd(fd);
        return this;
    }

    public Connection SetReactor(Engine.Engine.Reactor reactor)
    {
        base.SetReactor(reactor);
        return this;
    }

    // =========================================================================
    // Pooling / lifecycle
    // =========================================================================

    public override void Clear()
    {
        base.Clear();

        // Per-connection buffer ring: reset pointers but keep slab/arrays for pool reuse
        BufRing = null;
        Bgid = 0;
        IncrementalMode = false;
    }

    public override void ReturnRing(ushort bufferId)
    {
        if (IncrementalMode)
            ((Engine.Engine.Reactor)Reactor).EnqueueReturnQIncremental(ClientFd, bufferId);
        else
            Reactor.EnqueueReturnQ(bufferId);
    }

    public override void Dispose()
    {
        base.Dispose();

        // Free per-connection buffer ring slab
        if (BufRingSlab != null)
        {
            NativeMemory.AlignedFree(BufRingSlab);
            BufRingSlab = null;
        }
    }
}
