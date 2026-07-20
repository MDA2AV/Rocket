using System.Runtime.CompilerServices;
using static ioxide.Native;

namespace ioxide;

/// <summary>
/// TCP transport: SO_REUSEPORT listeners, multishot accept, and the stream-shaped recv/send
/// submits that drive <see cref="Connection"/>. Peer transports: Reactor.Udp.cs / Reactor.Quic.cs.
/// </summary>
public sealed unsafe partial class Reactor
{
    private void SubmitRecvMultishot(int fd, ushort gen, ushort bgid)
    {
        IoUringSqe* sqe = GetSqeOrFlush();
        Unsafe.InitBlockUnaligned(sqe, 0, 64);
        sqe->opcode    = IORING_OP_RECV;
        sqe->flags     = IOSQE_BUFFER_SELECT;
        sqe->ioprio    = IORING_RECV_MULTISHOT;
        sqe->fd        = fd;
        sqe->buf_index = bgid;
        sqe->user_data = Tag(KindRecv, gen, fd);
    }

    // Dispatch a send to this connection's strategy. A predictable per-connection branch (ZeroCopySend
    // is constant for the run; kTLS pins plain) instead of an indirect call - so SubmitSendImpl stays
    // inlinable on the hot send path.
    private void SubmitSend(Connection conn, int fd, ushort gen, byte* buf, uint len, uint opFlags)
    {
        if (conn.UseZc)
        {
            SubmitSendImpl(this, IORING_OP_SEND_ZC, fd, gen, buf, len, opFlags);
        }
        else
        {
            SubmitSendImpl(this, IORING_OP_SEND, fd, gen, buf, len, opFlags);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SubmitSendImpl(Reactor r, byte opcode, int fd, ushort gen, byte* buf, uint len, uint opFlags)
    {
        IoUringSqe* sqe = r.GetSqeOrFlush();
        Unsafe.InitBlockUnaligned(sqe, 0, 64);
        sqe->opcode    = opcode;
        sqe->fd        = fd;
        sqe->addr      = (ulong)buf;
        sqe->len       = len;
        sqe->op_flags  = opFlags;   // MSG_WAITALL by default; cleared for kTLS
        sqe->user_data = Tag(KindSend, gen, fd);
    }

    // Vectored send: one SQE gathers every write segment (primary + overflow) from the iovec the
    // connection prepared in BuildIovec. Plain SENDMSG (no zero-copy) for the segmented path.
    private void SubmitSendMsg(Connection conn, int fd, ushort gen)
    {
        IoUringSqe* sqe = GetSqeOrFlush();
        Unsafe.InitBlockUnaligned(sqe, 0, 64);
        sqe->opcode    = IORING_OP_SENDMSG;
        sqe->fd        = fd;
        sqe->addr      = (ulong)conn.MsgHdr;
        sqe->len       = 1;
        sqe->op_flags  = conn.SendOpFlags;   // MSG_WAITALL
        sqe->user_data = Tag(KindSend, gen, fd);
    }

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