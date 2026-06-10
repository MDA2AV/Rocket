using System.Runtime.InteropServices;

namespace RingPg;

/// <summary>libc bits for the one-time blocking connect + startup handshake (queries use the ring).</summary>
internal static unsafe class Native
{
    public const int AF_INET = 2, SOCK_STREAM = 1;
    public const int IPPROTO_TCP = 6, TCP_NODELAY = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct sockaddr_in
    {
        public ushort sin_family;
        public ushort sin_port;
        public uint sin_addr;
        public ulong sin_zero;
    }

    [DllImport("libc")] public static extern int socket(int domain, int type, int proto);
    [DllImport("libc")] public static extern int setsockopt(int fd, int level, int opt, void* val, uint len);
    [DllImport("libc", SetLastError = true)] public static extern int connect(int fd, sockaddr_in* addr, uint len);
    [DllImport("libc", SetLastError = true)] public static extern long send(int fd, void* buf, nuint n, int flags);
    [DllImport("libc", SetLastError = true)] public static extern long recv(int fd, void* buf, nuint n, int flags);

    public static ushort Htons(ushort x) => (ushort)((x << 8) | (x >> 8));
}
