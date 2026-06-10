namespace terraform.Ring;

internal static class Constants
{
    // =========================================================================
    // Syscall numbers (x86_64)
    // =========================================================================
    internal const long SYS_io_uring_setup    = 425;
    internal const long SYS_io_uring_enter    = 426;
    internal const long SYS_io_uring_register = 427;

    // =========================================================================
    // io_uring setup flags (IORING_SETUP_*)
    // =========================================================================
    internal const uint IORING_SETUP_IOPOLL             = 1u << 0;
    internal const uint IORING_SETUP_SQPOLL             = 1u << 1;
    internal const uint IORING_SETUP_SQ_AFF             = 1u << 2;
    internal const uint IORING_SETUP_CQSIZE             = 1u << 3;
    internal const uint IORING_SETUP_CLAMP              = 1u << 4;
    internal const uint IORING_SETUP_SINGLE_ISSUER      = 1u << 12;
    internal const uint IORING_SETUP_DEFER_TASKRUN      = 1u << 13;
    internal const uint IORING_SETUP_NO_MMAP            = 1u << 14;
    internal const uint IORING_SETUP_REGISTERED_FD_ONLY = 1u << 15;

    // =========================================================================
    // io_uring opcodes (IORING_OP_*)
    // =========================================================================
    internal const byte IORING_OP_ACCEPT       = 13;
    internal const byte IORING_OP_ASYNC_CANCEL = 14;
    internal const byte IORING_OP_SEND         = 26;
    internal const byte IORING_OP_RECV         = 27;

    // =========================================================================
    // SQE flags
    // =========================================================================
    internal const byte IOSQE_BUFFER_SELECT = 1 << 5; // 0x20

    // =========================================================================
    // Multishot ioprio flags
    // =========================================================================
    internal const ushort IORING_ACCEPT_MULTISHOT = 1;
    internal const ushort IORING_RECV_MULTISHOT   = 2;

    // =========================================================================
    // CQE flags
    // =========================================================================
    internal const uint IORING_CQE_F_BUFFER   = 1u << 0;
    internal const uint IORING_CQE_F_MORE     = 1u << 1;
    internal const uint IORING_CQE_F_BUF_MORE = 1u << 4;
    internal const int  IORING_CQE_BUFFER_SHIFT = 16;

    // =========================================================================
    // Cancel flags
    // =========================================================================
    internal const int IORING_ASYNC_CANCEL_ALL = 1 << 0;

    // =========================================================================
    // io_uring_enter flags
    // =========================================================================
    internal const uint IORING_ENTER_GETEVENTS = 1u << 0;
    internal const uint IORING_ENTER_EXT_ARG   = 1u << 3;

    // =========================================================================
    // io_uring_register opcodes
    // =========================================================================
    internal const uint IORING_REGISTER_PBUF_RING   = 22;
    internal const uint IORING_UNREGISTER_PBUF_RING = 23;

    // =========================================================================
    // Mmap offsets
    // =========================================================================
    internal const long IORING_OFF_SQ_RING = 0;
    internal const long IORING_OFF_SQES    = 0x10000000;

    // =========================================================================
    // Mmap flags
    // =========================================================================
    internal const int PROT_READ    = 1;
    internal const int PROT_WRITE   = 2;
    internal const int MAP_SHARED   = 1;
    internal const int MAP_POPULATE = 0x8000;

    // Socket constants are in ABI/Libc.cs to avoid duplication

    // =========================================================================
    // Feature flags returned by io_uring_setup
    // =========================================================================
    internal const uint IORING_FEAT_SINGLE_MMAP = 1u << 0;

    // =========================================================================
    // User-data packing
    // =========================================================================
    internal enum UdKind : uint
    {
        Accept = 1,
        Recv   = 2,
        Send   = 3,
        Cancel = 4
    }

    internal static ulong PackUd(UdKind k, int fd) => ((ulong)k << 32) | (uint)fd;
    internal static UdKind UdKindOf(ulong ud) => (UdKind)(ud >> 32);
    internal static int UdFdOf(ulong ud) => (int)(ud & 0xffffffff);
}
