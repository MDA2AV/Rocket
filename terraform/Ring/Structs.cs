using System.Runtime.InteropServices;

namespace terraform.Ring;

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
/// Submission Queue Entry (64 bytes). Explicit layout for union fields.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 64)]
internal struct IoUringSqe
{
    [FieldOffset(0)]  public byte   opcode;
    [FieldOffset(1)]  public byte   flags;
    [FieldOffset(2)]  public ushort ioprio;
    [FieldOffset(4)]  public int    fd;
    [FieldOffset(8)]  public ulong  off;       // off / addr2
    [FieldOffset(16)] public ulong  addr;      // addr / splice_off_in
    [FieldOffset(24)] public uint   len;
    [FieldOffset(28)] public uint   op_flags;  // rw_flags / send_flags / accept_flags etc.
    [FieldOffset(32)] public ulong  user_data;
    [FieldOffset(40)] public ushort buf_index; // buf_index / buf_group
    [FieldOffset(42)] public ushort personality;
    [FieldOffset(44)] public int    splice_fd_in; // file_index
    [FieldOffset(48)] public ulong  addr3;
    [FieldOffset(56)] public ulong  __pad2;
}

/// <summary>
/// Completion Queue Entry (16 bytes).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct IoUringCqe
{
    public ulong user_data;
    public int   res;
    public uint  flags;
}

/// <summary>
/// Kernel timespec (16 bytes).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct KernelTimespec
{
    public long tv_sec;
    public long tv_nsec;
}

/// <summary>
/// Extended argument for io_uring_enter with IORING_ENTER_EXT_ARG (24 bytes).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct IoUringGeteventsArg
{
    public ulong sigmask;
    public uint  sigmask_sz;
    public uint  pad;
    public ulong ts; // pointer to KernelTimespec, passed as u64
}

/// <summary>
/// Buffer ring entry (16 bytes).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct IoUringBuf
{
    public ulong  addr;
    public uint   len;
    public ushort bid;
    public ushort resv;
}

/// <summary>
/// Buffer ring registration (40 bytes).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct IoUringBufReg
{
    public ulong ring_addr;
    public uint  ring_entries;
    public ushort bgid;
    public ushort flags;
    public ulong resv0;
    public ulong resv1;
    public ulong resv2;
}
