using System.Runtime.InteropServices;

namespace ioxide.pg;

/// <summary>
/// The handful of libc calls used for the one-time, blocking connect and startup handshake.
/// Everything after the handshake goes through the ring, not through these.
/// </summary>
internal static unsafe class Native
{
    public const int AF_INET = 2;
    public const int SOCK_STREAM = 1;

    public const int IPPROTO_TCP = 6;
    public const int TCP_NODELAY = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct sockaddr_in
    {
        public ushort sin_family;
        public ushort sin_port;
        public uint sin_addr;
        public ulong sin_zero;
    }

    [DllImport("libc")]
    public static extern int socket(int domain, int type, int protocol);

    [DllImport("libc")]
    public static extern int setsockopt(int fd, int level, int option, void* value, uint length);

    [DllImport("libc", SetLastError = true)]
    public static extern int connect(int fd, sockaddr_in* address, uint length);

    [DllImport("libc", SetLastError = true)]
    public static extern long send(int fd, void* buffer, nuint count, int flags);

    [DllImport("libc", SetLastError = true)]
    public static extern long recv(int fd, void* buffer, nuint count, int flags);

    /// <summary>Host-to-network short — Postgres expects the port in network byte order.</summary>
    public static ushort Htons(ushort value)
    {
        return (ushort)((value << 8) | (value >> 8));
    }
}
