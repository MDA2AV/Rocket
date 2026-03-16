using System.Runtime.InteropServices;

namespace terraform.ABI;

internal static unsafe class Libc
{
    // =========================================================================
    // libc socket/net interop
    // =========================================================================

    [DllImport("libc")] internal static extern int socket(int domain, int type, int proto);
    [DllImport("libc")] internal static extern int setsockopt(int fd, int level, int optname, void* optval, uint optlen);
    [DllImport("libc")] internal static extern int bind(int fd, sockaddr_in* addr, uint len);
    [DllImport("libc", SetLastError = true)] internal static extern int bind(int fd, sockaddr_in6* addr, uint len);
    [DllImport("libc")] internal static extern int listen(int fd, int backlog);
    [DllImport("libc")] internal static extern int fcntl(int fd, int cmd, int arg);
    [DllImport("libc")] internal static extern int close(int fd);
    [DllImport("libc")] internal static extern int inet_pton(int af, sbyte* src, void* dst);

    // =========================================================================
    // Socket constants
    // =========================================================================

    internal const int AF_INET      = 2;
    internal const int SOCK_STREAM  = 1;
    internal const int SOL_SOCKET   = 1;
    internal const int SO_REUSEADDR = 2;
    internal const int SO_REUSEPORT = 15;

    internal const int AF_INET6     = 10;
    internal const int IPPROTO_IPV6 = 41;
    internal const int IPV6_V6ONLY  = 26;

    internal const int IPPROTO_TCP  = 6;
    internal const int TCP_NODELAY  = 1;

    internal const int F_GETFL      = 3;
    internal const int F_SETFL      = 4;
    internal const int O_NONBLOCK   = 0x800;
    internal const int SOCK_NONBLOCK = 0x800;

    // =========================================================================
    // Structs
    // =========================================================================

    [StructLayout(LayoutKind.Sequential)]
    internal struct in_addr
    {
        public uint s_addr;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct sockaddr_in
    {
        public ushort  sin_family;
        public ushort  sin_port;
        public in_addr sin_addr;
        public fixed byte sin_zero[8];
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct in6_addr
    {
        public fixed byte s6_addr[16];
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct sockaddr_in6
    {
        public ushort   sin6_family;
        public ushort   sin6_port;
        public uint     sin6_flowinfo;
        public in6_addr sin6_addr;
        public uint     sin6_scope_id;
    }

    internal static ushort Htons(ushort x) => (ushort)((x << 8) | (x >> 8));
}
