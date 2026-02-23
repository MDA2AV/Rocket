using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using static zerg.ABI.ABI;

namespace zerg.Engine;

public sealed unsafe partial class Engine 
{
    /// <summary>
    /// Responsible for creating and managing listening sockets for incoming connections.
    /// Supports:
    ///  - IPv4-only listeners (AF_INET)
    ///  - Dual-stack IPv6 listeners (AF_INET6 + IPv4-mapped addresses)
    /// </summary>
    public partial class Acceptor
    {
        /// <summary>
        /// Creates an IPv4-only listening socket bound to the given IPv4 address and port.
        /// This socket will accept ONLY IPv4 connections.
        /// </summary>
        private int CreateIPv4ListenerSocket(string ip, ushort port)
        {
            int lfd = socket(AF_INET, SOCK_STREAM, 0);
            if (lfd < 0) ThrowErrno("socket");

            try
            {
                int one = 1;

                if (setsockopt(lfd, SOL_SOCKET, SO_REUSEADDR, &one, (uint)sizeof(int)) < 0)
                    ThrowErrno("setsockopt(SO_REUSEADDR)");

                // Linux only; great for multi-reactor accept, but fails on some platforms/kernels/configs.
                if (setsockopt(lfd, SOL_SOCKET, SO_REUSEPORT, &one, (uint)sizeof(int)) < 0)
                    ThrowErrno("setsockopt(SO_REUSEPORT)");

                // TCP_NODELAY on a listening socket is not useful.

                sockaddr_in addr = default;
                addr.sin_family = (ushort)AF_INET;
                addr.sin_port = Htons(port);

                // Better: use ASCII bytes, not UTF8.
                byte[] ipb = Encoding.ASCII.GetBytes(ip + "\0");
                fixed (byte* pip = ipb)
                {
                    int rc = inet_pton(AF_INET, (sbyte*)pip, &addr.sin_addr);
                    if (rc == 0) throw new ArgumentException($"Invalid IPv4 address: {ip}", nameof(ip));
                    if (rc < 0) ThrowErrno("inet_pton");
                }

                if (bind(lfd, &addr, (uint)sizeof(sockaddr_in)) < 0)
                    ThrowErrno("bind");

                if (listen(lfd, _engine.Options.Backlog) < 0)
                    ThrowErrno("listen");

                int fl = fcntl(lfd, F_GETFL, 0);
                if (fl < 0) ThrowErrno("fcntl(F_GETFL)");

                if (fcntl(lfd, F_SETFL, fl | O_NONBLOCK) < 0)
                    ThrowErrno("fcntl(F_SETFL,O_NONBLOCK)");

                return lfd;
            }
            catch
            {
                close(lfd);
                throw;
            }
        }

        /// <summary>
        /// Creates a dual-stack IPv6 listening socket that can accept:
        ///   - native IPv6 connections
        ///   - IPv4 connections (as IPv4-mapped IPv6 addresses)
        ///
        /// Behavior:
        ///   "0.0.0.0"  -> binds to "::" (accepts all IPv4 + IPv6)
        ///   IPv6 addr -> binds that IPv6 (still dual-stack)
        ///   IPv4 addr -> binds to ::ffff:a.b.c.d (IPv4-mapped)
        /// </summary>
        private int CreateListenerSocketDualStack(string ip, ushort port)
        {
            // Interpret empty/"*" as any
            if (string.IsNullOrEmpty(ip) || ip == "*")
                ip = "0.0.0.0";

            int lfd = socket(AF_INET6, SOCK_STREAM, 0);
            if (lfd < 0) ThrowErrno("socket(AF_INET6)");

            try
            {
                int one = 1;
                int zero = 0;

                if (setsockopt(lfd, SOL_SOCKET, SO_REUSEADDR, &one, (uint)sizeof(int)) < 0)
                    ThrowErrno("setsockopt(SO_REUSEADDR)");

                if (setsockopt(lfd, SOL_SOCKET, SO_REUSEPORT, &one, (uint)sizeof(int)) < 0)
                    ThrowErrno("setsockopt(SO_REUSEPORT)");

                // Dual-stack (allow IPv4-mapped)
                if (setsockopt(lfd, IPPROTO_IPV6, IPV6_V6ONLY, &zero, (uint)sizeof(int)) < 0)
                    ThrowErrno("setsockopt(IPV6_V6ONLY=0)");

                sockaddr_in6 addr6 = default;
                addr6.sin6_family = (ushort)AF_INET6;
                addr6.sin6_port = Htons(port);
                addr6.sin6_flowinfo = 0;
                addr6.sin6_scope_id = 0;

                // Special-case IPv4 ANY -> IPv6 ANY (::)
                if (ip == "0.0.0.0")
                {
                    // addr6.sin6_addr already zero => ::
                }
                else
                {
                    // Try IPv6 first
                    bool parsed = false;

                    byte[] ipb = Encoding.ASCII.GetBytes(ip + "\0");
                    fixed (byte* pip = ipb)
                    {
                        int rc6 = inet_pton(AF_INET6, (sbyte*)pip, &addr6.sin6_addr);
                        if (rc6 == 1)
                        {
                            parsed = true;
                        }
                        else if (rc6 < 0)
                        {
                            ThrowErrno("inet_pton(AF_INET6)");
                        }

                        if (!parsed)
                        {
                            // Parse as IPv4
                            in_addr v4 = default;
                            int rc4 = inet_pton(AF_INET, (sbyte*)pip, &v4);
                            if (rc4 == 1)
                            {
                                addr6.sin6_addr = MapIPv4ToMappedIPv6(v4);
                                parsed = true;
                            }
                            else if (rc4 < 0)
                            {
                                ThrowErrno("inet_pton(AF_INET)");
                            }
                        }
                    }

                    if (!parsed)
                        throw new ArgumentException($"Invalid IP address: {ip}", nameof(ip));
                }

                if (bind(lfd, &addr6, (uint)sizeof(sockaddr_in6)) < 0)
                    ThrowErrno("bind(AF_INET6)");

                if (listen(lfd, _engine.Options.Backlog) < 0)
                    ThrowErrno("listen");

                int fl = fcntl(lfd, F_GETFL, 0);
                if (fl < 0) ThrowErrno("fcntl(F_GETFL)");

                if (fcntl(lfd, F_SETFL, fl | O_NONBLOCK) < 0)
                    ThrowErrno("fcntl(F_SETFL,O_NONBLOCK)");

                return lfd;
            }
            catch
            {
                close(lfd);
                throw;
            }
        }

        /// <summary>
        /// Converts an IPv4 address to an IPv4-mapped IPv6 address (::ffff:a.b.c.d).
        /// This is the standard mechanism used by dual-stack IPv6 sockets.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static in6_addr MapIPv4ToMappedIPv6(ABI.ABI.in_addr v4)
        {
            in6_addr v6 = default;

            // ::ffff:0:0/96 prefix
            v6.s6_addr[10] = 0xFF;
            v6.s6_addr[11] = 0xFF;

            // v4.s_addr is in network byte order already.
            // Copy its 4 bytes into v6[12..15].
            uint s = v4.s_addr;
            v6.s6_addr[12] = (byte)(s >> 24);
            v6.s6_addr[13] = (byte)(s >> 16);
            v6.s6_addr[14] = (byte)(s >> 8);
            v6.s6_addr[15] = (byte)(s);

            return v6;
        }

        /// <summary>
        /// Throws a managed exception using the last native errno.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowErrno(string op)
        {
            int err = Marshal.GetLastPInvokeError();
            throw new InvalidOperationException($"{op} failed, errno={err}");
        }
    }
}