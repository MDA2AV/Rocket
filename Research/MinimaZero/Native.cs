using System.Runtime.InteropServices;

namespace MinimaZero;

/// <summary>
/// All native interop in one file: io_uring syscalls, libc socket calls,
/// the kernel struct layouts they expect, and the constants needed to
/// drive a minimal io_uring loop -here in its zero-copy receive (zcrx)
/// variant.
/// </summary>
internal static unsafe class Native {
    private const long SYS_IO_URING_SETUP    = 425;
    private const long SYS_IO_URING_ENTER    = 426;
    private const long SYS_IO_URING_REGISTER = 427;

    public const byte IORING_OP_ACCEPT = 13;
    public const byte IORING_OP_SEND   = 26;
    public const byte IORING_OP_RECV   = 27;

    // zcrx: receive directly into NIC-DMA'd userspace memory. Mainline value
    // is 58 (after IORING_OP_LISTEN=57). VERIFY against your kernel header.
    public const byte IORING_OP_RECV_ZC = 58;

    public const uint IORING_ENTER_GETEVENTS = 1u << 0;
    public const long IORING_OFF_SQ_RING = 0;
    public const long IORING_OFF_SQES    = 0x10000000;

    // Multishot / buffer-ring goodies.
    public const ushort IORING_ACCEPT_MULTISHOT = 1 << 0;
    public const ushort IORING_RECV_MULTISHOT   = 1 << 1;
    public const byte   IOSQE_BUFFER_SELECT     = 1 << 5;
    public const uint   IORING_CQE_F_BUFFER     = 1u << 0;
    public const uint   IORING_CQE_F_MORE       = 1u << 1;
    public const int    IORING_CQE_BUFFER_SHIFT = 16;

    // Setup flags. SINGLE_ISSUER tells the kernel only one thread will submit
    // to this ring (skips locking on the SQ). DEFER_TASKRUN defers completion
    // processing until io_uring_enter(GETEVENTS). zcrx additionally requires a
    // 32-byte CQE so it can append struct io_uring_zcrx_cqe after each cqe.
    public const uint IORING_SETUP_CQE32         = 1u << 11;
    public const uint IORING_SETUP_SINGLE_ISSUER = 1u << 12;
    public const uint IORING_SETUP_DEFER_TASKRUN = 1u << 13;

    // -------------------------------------------------------------------
    // zcrx registration / ABI  (VERIFY against target kernel uapi header)
    // -------------------------------------------------------------------

    /// <summary>io_uring_register opcode that registers a zcrx interface
    /// queue. Mainline (~6.15) value = 32.</summary>
    public const uint IORING_REGISTER_ZCRX_IFQ = 32;

    /// <summary>An rcqe.off splits into [area_id | area_offset] at this bit.
    /// Low 48 bits = byte offset within the area; high bits = area id.</summary>
    public const int  IORING_ZCRX_AREA_SHIFT = 48;
    public const ulong IORING_ZCRX_AREA_MASK = ~((1UL << IORING_ZCRX_AREA_SHIFT) - 1);

    /// <summary>io_uring_region_desc.flags: region memory is user-provided
    /// (we mmap it and pass the address) rather than kernel-allocated.</summary>
    public const uint IORING_MEM_REGION_TYPE_USER = 1u;

    public const int PROT_READ    = 1;
    public const int PROT_WRITE   = 2;
    public const int MAP_SHARED   = 1;
    public const int MAP_ANONYMOUS = 0x20;
    public const int MAP_POPULATE = 0x8000;

    public const int AF_INET      = 2;
    public const int SOCK_STREAM  = 1;
    public const int SOL_SOCKET   = 1;
    public const int SO_REUSEADDR = 2;
    public const int SO_REUSEPORT = 15;

    [DllImport("libc", EntryPoint = "syscall")]
    private static extern long syscall3(long nr, uint a1, IoUringParams* a2);

    [DllImport("libc", EntryPoint = "syscall")]
    private static extern long syscall6(long nr, uint a1, uint a2, uint a3, uint a4, void* a5, nuint a6);

    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static extern long syscall4(long nr, uint a1, uint a2, void* a3, uint a4);

    public static int io_uring_setup(uint entries, IoUringParams* p) =>
        (int)syscall3(SYS_IO_URING_SETUP, entries, p);

    public static int io_uring_enter(int fd, uint toSubmit, uint minComplete, uint flags) =>
        (int)syscall6(SYS_IO_URING_ENTER, (uint)fd, toSubmit, minComplete, flags, null, 0);

    public static int io_uring_register(int fd, uint opcode, void* arg, uint nrArgs) =>
        (int)syscall4(SYS_IO_URING_REGISTER, (uint)fd, opcode, arg, nrArgs);

    [DllImport("libc")] public static extern void* mmap(void* addr, nuint length, int prot, int flags, int fd, long offset);
    [DllImport("libc")] public static extern int   munmap(void* addr, nuint length);
    [DllImport("libc")] public static extern int   close(int fd);
    [DllImport("libc")] public static extern int   socket(int domain, int type, int proto);
    [DllImport("libc")] public static extern int   bind(int fd, sockaddr_in* addr, uint len);
    [DllImport("libc")] public static extern int   listen(int fd, int backlog);
    [DllImport("libc")] public static extern int   setsockopt(int fd, int level, int optname, void* optval, uint optlen);
    [DllImport("libc")] public static extern uint  if_nametoindex(string ifname);

    public static ushort Htons(ushort x) => (ushort)((x << 8) | (x >> 8));

    /// <summary>Anonymous, page-aligned, shared mapping (fd = -1).</summary>
    public static void* MmapAnon(nuint length) =>
        mmap(null, length, PROT_READ | PROT_WRITE, MAP_SHARED | MAP_ANONYMOUS | MAP_POPULATE, -1, 0);

    // Kernel struct layouts (must match include/uapi/linux/io_uring.h)
    [StructLayout(LayoutKind.Sequential)]
    public struct SqRingOffsets {
        public uint head, tail, ring_mask, ring_entries, flags, dropped, array, resv1;
        public ulong resv2;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CqRingOffsets {
        public uint head, tail, ring_mask, ring_entries, overflow, cqes, flags, resv1;
        public ulong resv2;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IoUringParams {
        public uint sq_entries, cq_entries, flags, sq_thread_cpu, sq_thread_idle;
        public uint features, wq_fd, resv0, resv1, resv2;
        public SqRingOffsets sq_off;
        public CqRingOffsets cq_off;
    }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    public struct IoUringSqe {
        [FieldOffset(0)]  public byte   opcode;
        [FieldOffset(1)]  public byte   flags;
        [FieldOffset(2)]  public ushort ioprio;
        [FieldOffset(4)]  public int    fd;
        [FieldOffset(8)]  public ulong  off;
        [FieldOffset(16)] public ulong  addr;
        [FieldOffset(24)] public uint   len;
        [FieldOffset(28)] public uint   op_flags;
        [FieldOffset(32)] public ulong  user_data;
        [FieldOffset(40)] public ushort buf_index;
        [FieldOffset(42)] public ushort personality;
        [FieldOffset(44)] public int    splice_fd_in;
        [FieldOffset(48)] public ulong  addr3;
        [FieldOffset(56)] public ulong  __pad2;
    }

    /// <summary>
    /// Base completion. With IORING_SETUP_CQE32 the CQ stride is 32 bytes:
    /// this 16-byte head is immediately followed by an
    /// <see cref="IoUringZcrxCqe"/> for zcrx completions.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct IoUringCqe {
        public ulong user_data;
        public int   res;
        public uint  flags;
    }

    /// <summary>Trailing 16 bytes of a CQE32 for an IORING_OP_RECV_ZC
    /// completion. <c>off</c> = (area_id &lt;&lt; 48) | area_offset.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct IoUringZcrxCqe {
        public ulong off;
        public ulong __pad;
    }

    /// <summary>Refill-queue entry: hands a chunk back to the kernel so the
    /// NIC can DMA into it again. Userspace is the producer.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct IoUringZcrxRqe {
        public ulong off;
        public uint  len;
        public uint  __pad;
    }

    /// <summary>Kernel-filled byte offsets of the refill ring's control
    /// words and rqe array within the region we registered.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct IoUringZcrxOffsets {
        public uint  head;
        public uint  tail;
        public uint  rqes;
        public uint  __resv2;
        public ulong __resv0;
        public ulong __resv1;
    }

    /// <summary>Describes the payload area the NIC DMAs into.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct IoUringZcrxAreaReg {
        public ulong addr;
        public ulong len;
        public ulong rq_area_token;   // kernel-filled
        public uint  flags;
        public uint  __resv1;
        public ulong __resv2a;
        public ulong __resv2b;
    }

    /// <summary>Describes a shared memory region (here: the refill ring).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct IoUringRegionDesc {
        public ulong user_addr;
        public ulong size;
        public uint  flags;
        public uint  id;
        public ulong mmap_offset;
        public ulong __resv0;
        public ulong __resv1;
        public ulong __resv2;
        public ulong __resv3;
    }

    /// <summary>Argument to IORING_REGISTER_ZCRX_IFQ. Binds one interface
    /// Rx queue (if_idx, if_rxq) to one refill ring + one payload area.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct IoUringZcrxIfqReg {
        public uint if_idx;
        public uint if_rxq;
        public uint rq_entries;
        public uint flags;
        public ulong area_ptr;     // &IoUringZcrxAreaReg
        public ulong region_ptr;   // &IoUringRegionDesc
        public IoUringZcrxOffsets offsets;
        public uint zcrx_id;       // kernel-filled
        public uint __resv2;
        public ulong __resv0;
        public ulong __resv1;
        public ulong __resv3;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct in_addr { public uint s_addr; }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct sockaddr_in {
        public ushort  sin_family;
        public ushort  sin_port;
        public in_addr sin_addr;
        public fixed byte sin_zero[8];
    }
}
