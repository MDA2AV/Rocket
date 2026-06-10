namespace Myr.Core;

internal static unsafe class Native
{
    // libc
    #region libc
    
    [DllImport("libc")] internal static extern int socket(int domain, int type, int proto);
    [DllImport("libc")] internal static extern int setsockopt(int fd, int level, int optname, void* optval, uint optlen);
    [DllImport("libc")] internal static extern int bind(int fd, SockaddrIn* addr, uint len);
    [DllImport("libc", SetLastError = true)] internal static extern int bind(int fd, SockaddrIn6* addr, uint len);
    [DllImport("libc")] internal static extern int listen(int fd, int backlog);
    [DllImport("libc")] internal static extern int fcntl(int fd, int cmd, int arg);
    [DllImport("libc")] internal static extern int close(int fd);
    [DllImport("libc")] internal static extern int inet_pton(int af, sbyte* src, void* dst);
    
    [DllImport("libc", EntryPoint = "syscall")] private static extern long syscall3(long nr, uint entries, IoUringParams* p);
    [DllImport("libc", EntryPoint = "syscall")] private static extern long syscall6(long nr, uint fd, uint to_submit, uint min_complete, uint flags, void* arg, nuint argsz);
    [DllImport("libc", EntryPoint = "syscall")] private static extern long syscall4(long nr, uint fd, uint opcode, void* arg, uint nr_args);
    [DllImport("libc")] internal static extern void* mmap(void* addr, nuint length, int prot, int flags, int fd, long offset);
    [DllImport("libc")] internal static extern int munmap(void* addr, nuint length);
    
    internal const int AF_INET      = 2;
    internal const int SOCK_STREAM  = 1;
    internal const int SOL_SOCKET   = 1;
    internal const int SO_REUSEADDR = 2;
    internal const int SO_REUSEPORT = 15;

    internal const int AF_INET6     = 10;
    internal const int IPPROTO_IPV6 = 41;
    internal const int IPV6_V6_ONLY  = 26;

    internal const int IPPROTO_TCP  = 6;
    internal const int TCP_NODELAY  = 1;

    internal const int F_GETFL      = 3;
    internal const int F_SETFL      = 4;
    internal const int O_NONBLOCK   = 0x800;
    internal const int SOCK_NONBLOCK = 0x800;

    [StructLayout(LayoutKind.Sequential)] internal struct InAddr { public uint s_addr; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SockaddrIn {
        public ushort  sin_family;
        public ushort  sin_port;
        public InAddr sin_addr;
        public fixed byte sin_zero[8];
    }

    [StructLayout(LayoutKind.Sequential)] internal struct In6Addr { public fixed byte s6_addr[16]; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SockaddrIn6 {
        public ushort   sin6_family;
        public ushort   sin6_port;
        public uint     sin6_flowinfo;
        public In6Addr sin6_addr;
        public uint     sin6_scope_id;
    }

    internal static ushort Htons(ushort x) => (ushort)((x << 8) | (x >> 8));
    
    #endregion
    
    // io_uring
    #region io_uring
    
    internal const long SYS_io_uring_setup    = 425;
    internal const long SYS_io_uring_enter    = 426;
    internal const long SYS_io_uring_register = 427;
    
    internal const uint IORING_SETUP_IOPOLL             = 1u << 0;
    internal const uint IORING_SETUP_SQPOLL             = 1u << 1;
    internal const uint IORING_SETUP_SQ_AFF             = 1u << 2;
    internal const uint IORING_SETUP_CQSIZE             = 1u << 3;
    internal const uint IORING_SETUP_CLAMP              = 1u << 4;
    internal const uint IORING_SETUP_SINGLE_ISSUER      = 1u << 12;
    internal const uint IORING_SETUP_DEFER_TASKRUN      = 1u << 13;
    internal const uint IORING_SETUP_NO_MMAP            = 1u << 14;
    internal const uint IORING_SETUP_REGISTERED_FD_ONLY = 1u << 15;
    
    internal const byte IORING_OP_ACCEPT       = 13;
    internal const byte IORING_OP_ASYNC_CANCEL = 14;
    internal const byte IORING_OP_SEND         = 26;
    internal const byte IORING_OP_RECV         = 27;
    
    internal const byte IOSQE_BUFFER_SELECT = 1 << 5; // 0x20
    
    internal const ushort IORING_ACCEPT_MULTISHOT = 1;
    internal const ushort IORING_RECV_MULTISHOT   = 2;
    
    internal const uint IORING_CQE_F_BUFFER   = 1u << 0;
    internal const uint IORING_CQE_F_MORE     = 1u << 1;
    internal const uint IORING_CQE_F_BUF_MORE = 1u << 4;
    internal const int  IORING_CQE_BUFFER_SHIFT = 16;
    
    internal const int IORING_ASYNC_CANCEL_ALL = 1 << 0;
    
    internal const uint IORING_ENTER_GETEVENTS = 1u << 0;
    internal const uint IORING_ENTER_EXT_ARG   = 1u << 3;
    
    internal const uint IORING_REGISTER_PBUF_RING   = 22;
    internal const uint IORING_UNREGISTER_PBUF_RING = 23;
    
    internal const long IORING_OFF_SQ_RING = 0;
    internal const long IORING_OFF_SQES    = 0x10000000;
    
    internal const int PROT_READ    = 1;
    internal const int PROT_WRITE   = 2;
    internal const int MAP_SHARED   = 1;
    internal const int MAP_POPULATE = 0x8000;
    
    internal const uint IORING_FEAT_SINGLE_MMAP = 1u << 0;
    
    internal enum UdKind : uint {
        Accept = 1,
        Recv   = 2,
        Send   = 3,
        Cancel = 4
    }

    internal static ulong PackUd(UdKind k, int fd) => ((ulong)k << 32) | (uint)fd;
    internal static UdKind UdKindOf(ulong ud) => (UdKind)(ud >> 32);
    internal static int UdFdOf(ulong ud) => (int)(ud & 0xffffffff);
    
    /// <summary>
    /// io_uring_setup(entries, params) → ring fd or -errno.
    /// </summary>
    internal static int io_uring_setup(uint entries, IoUringParams* p) {
        long ret = syscall3(SYS_io_uring_setup, entries, p);
        return (int)ret;
    }

    /// <summary>
    /// io_uring_enter(fd, to_submit, min_complete, flags, arg, argsz) → submitted or -errno.
    /// </summary>
    internal static int io_uring_enter(int fd, uint toSubmit, uint minComplete, uint flags, void* arg, nuint argsz) {
        long ret = syscall6(SYS_io_uring_enter, (uint)fd, toSubmit, minComplete, flags, arg, argsz);
        return (int)ret;
    }

    /// <summary>
    /// io_uring_register(fd, opcode, arg, nr_args) → 0 or -errno.
    /// </summary>
    internal static int io_uring_register(int fd, uint opcode, void* arg, uint nrArgs) {
        long ret = syscall4(SYS_io_uring_register, (uint)fd, opcode, arg, nrArgs);
        return (int)ret;
    }
    
    #endregion
}