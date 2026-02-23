using System.Runtime.InteropServices;

namespace zerg.ABI;

public static unsafe partial class ABI{
    // ------------------------------------------------------------------------------------
    //  libc SOCKET/NET INTEROP
    // ------------------------------------------------------------------------------------
    /// <summary>
    /// Creates a socket (e.g., <see cref="AF_INET"/> + <see cref="SOCK_STREAM"/>).
    /// </summary>
    [DllImport("libc")] internal static extern int socket(int domain, int type, int proto);
    /// <summary>
    /// Sets a socket option; pass pointers to option data via <paramref name="optval"/>.
    /// Returns 0 on success, -1 on error (check errno).
    /// </summary>
    [DllImport("libc")] internal static extern int setsockopt(int fd, int level, int optname, void* optval, uint optlen);
    /// <summary>
    /// Binds a socket to the given address (IPv4).
    /// Returns 0 on success, -1 on error (check errno).
    /// </summary>
    [DllImport("libc")] internal static extern int bind(int fd, sockaddr_in* addr, uint len);
    /// <summary>
    /// Binds a socket to the given address (IPv6).
    /// Returns 0 on success, -1 on error (check errno).
    /// </summary>
    [DllImport("libc", SetLastError = true)] internal static extern int bind(int fd, sockaddr_in6* addr, uint len);
    /// <summary>
    /// Marks socket as passive (listening). <paramref name="backlog"/> is the pending queue size.
    /// Returns 0 on success, -1 on error.
    /// </summary>
    [DllImport("libc")] internal static extern int listen(int fd, int backlog);
    /// <summary>
    /// File control (e.g., set O_NONBLOCK). See <see cref="F_GETFL"/>, <see cref="F_SETFL"/>.
    /// Returns flags/result on GET, 0 on success for SET, or -1 on error.
    /// </summary>
    [DllImport("libc")] internal static extern int fcntl(int fd, int cmd, int arg);
    /// <summary>
    /// Closes a file descriptor. Returns 0 on success, -1 on error.
    /// </summary>
    [DllImport("libc")] internal static extern int close(int fd);
    /// <summary>
    /// Converts text IP (&quot;0.0.0.0&quot;, &quot;127.0.0.1&quot;, etc.) to binary form into <paramref name="dst"/>.
    /// Returns 1 on success, 0 on invalid text, or -1 on error (errno set).
    /// </summary>
    [DllImport("libc")] internal static extern int inet_pton(int af, sbyte* src, void* dst);
    // ----- socket constants -----
    internal const int AF_INET      = 2;
    internal const int SOCK_STREAM  = 1;
    internal const int SOL_SOCKET   = 1;
    internal const int SO_REUSEADDR = 2;
    internal const int SO_REUSEPORT = 15;
    // ----- IPv6 constants -----
    internal const int AF_INET6     = 10;  // Linux: 10
    internal const int IPPROTO_IPV6 = 41;  // Linux: 41
    // IPv6 socket options
    internal const int IPV6_V6ONLY  = 26;  // from <netinet/in.h>

    internal const int IPPROTO_TCP  = 6;
    internal const int TCP_NODELAY  = 1;

    internal const int F_GETFL      = 3;
    internal const int F_SETFL      = 4;
    internal const int O_NONBLOCK   = 0x800;
    internal const int SOCK_NONBLOCK= 0x800; // for accept4/Socket flags (matches Linux)
    
    /// <summary>
    /// IPv4 address storage (network byte order).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct in_addr {
        public uint s_addr; // big-endian (network order)
    }
    /// <summary>
    /// IPv4 socket address.
    /// <para>
    /// Layout matches Linux <c>struct sockaddr_in</c>:
    /// sin_family (2), sin_port (2), sin_addr (4), padding (8).
    /// </para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct sockaddr_in {
        public ushort  sin_family;             // AF_INET
        public ushort  sin_port;               // big-endian (use Htons)
        public in_addr sin_addr;               // address in network byte order
        public fixed byte sin_zero[8];         // padding to match C layout
    }
    /// <summary>
    /// IPv6 address storage (network byte order).
    /// Matches Linux struct in6_addr (16 bytes).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct in6_addr
    {
        public fixed byte s6_addr[16];
    }
    /// <summary>
    /// IPv6 socket address.
    /// Layout matches Linux struct sockaddr_in6:
    ///   sin6_family (2)
    ///   sin6_port   (2)
    ///   sin6_flowinfo (4)
    ///   sin6_addr   (16)
    ///   sin6_scope_id (4)
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct sockaddr_in6
    {
        public ushort  sin6_family;   // AF_INET6
        public ushort  sin6_port;     // big-endian (Htons)
        public uint    sin6_flowinfo; // usually 0
        public in6_addr sin6_addr;    // IPv6 address
        public uint    sin6_scope_id; // usually 0
    }
    /// <summary>
    /// Converts a 16-bit host-order value to network byte order (big-endian).
    /// Equivalent to POSIX <c>htons</c>.
    /// </summary>
    internal static ushort Htons(ushort x) => (ushort)((x << 8) | (x >> 8));
}