namespace Myr.Core.IoUring;

/// <summary>
/// Parameters for io_uring_setup syscall. 120 bytes total.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct IoUringParams
{
    public uint sq_entries;
    public uint cq_entries;
    public uint flags;
    public uint sq_thread_cpu;
    public uint sq_thread_idle;
    public uint features;
    public uint wq_fd;
    public uint resv0;
    public uint resv1;
    public uint resv2;
    public SqRingOffsets sq_off;
    public CqRingOffsets cq_off;
}

/// <summary>
/// Offsets into the SQ ring mmap region, returned by io_uring_setup.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SqRingOffsets
{
    public uint head;
    public uint tail;
    public uint ring_mask;
    public uint ring_entries;
    public uint flags;
    public uint dropped;
    public uint array;
    public uint resv1;
    public ulong resv2;
}

/// <summary>
/// Offsets into the CQ ring mmap region, returned by io_uring_setup.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CqRingOffsets
{
    public uint head;
    public uint tail;
    public uint ring_mask;
    public uint ring_entries;
    public uint overflow;
    public uint cqes;
    public uint flags;
    public uint resv1;
    public ulong resv2;
}