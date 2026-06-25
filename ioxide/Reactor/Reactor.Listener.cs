using System.Runtime.CompilerServices;
using static ioxide.Native;

namespace ioxide;

public sealed unsafe partial class Reactor
{
    private void SubmitAcceptMultishot(int listenFd)
    {
        IoUringSqe* sqe = GetSqeOrFlush();
        Unsafe.InitBlockUnaligned(sqe, 0, 64);
        sqe->opcode    = IORING_OP_ACCEPT;
        sqe->ioprio    = IORING_ACCEPT_MULTISHOT;
        sqe->fd        = listenFd;
        sqe->user_data = Tag(KindAccept, 0, listenFd);
    }
    
    // Accept-time only; the listener table is tiny (Port + ExtraPorts).
    private ushort PortOf(int listenFd)
    {
        for (int i = 0; i < _listenFds.Length; i++)
        {
            if (_listenFds[i] == listenFd)
            {
                return _listenPorts[i];
            }
        }
        return _port;
    }
    
    // Per accepted socket - TCP_NODELAY doesn't reliably inherit from the listener.
    private static void SetNoDelay(int fd)
    {
        int one = 1;
        setsockopt(fd, IPPROTO_TCP, TCP_NODELAY, &one, sizeof(int));
    }

    private static int OpenReusePortListener(ushort port, int backlog, bool dualStack)
    {
        int fd = socket(dualStack ? AF_INET6 : AF_INET, SOCK_STREAM, 0);
        if (fd < 0)
        {
            throw new InvalidOperationException($"socket failed: {fd}");
        }

        int one = 1;
        setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &one, sizeof(int));
        setsockopt(fd, SOL_SOCKET, SO_REUSEPORT, &one, sizeof(int));

        if (dualStack)
        {
            // A single AF_INET6 listener bound to :: with IPV6_V6ONLY=0 accepts both IPv6 and
            // IPv4-mapped clients - one socket serves both families.
            int v6only = 0;
            setsockopt(fd, IPPROTO_IPV6, IPV6_V6ONLY, &v6only, sizeof(int));

            sockaddr_in6 addr6 = default;
            addr6.sin6_family = AF_INET6;
            addr6.sin6_port   = Htons(port);
            // sin6_addr left zero == in6addr_any (::)

            if (bind(fd, &addr6, (uint)sizeof(sockaddr_in6)) < 0)
            {
                throw new InvalidOperationException("bind failed");
            }
        }
        else
        {
            sockaddr_in addr = default;
            addr.sin_family      = AF_INET;
            addr.sin_port        = Htons(port);
            addr.sin_addr.s_addr = 0; // 0.0.0.0

            if (bind(fd, &addr, (uint)sizeof(sockaddr_in)) < 0)
            {
                throw new InvalidOperationException("bind failed");
            }
        }

        if (listen(fd, backlog) < 0)
        {
            throw new InvalidOperationException("listen failed");
        }

        return fd;
    }
}