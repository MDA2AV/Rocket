using System.Runtime.CompilerServices;
using static ioxide.Native;

namespace ioxide;

/// <summary>
/// TCP transport: SO_REUSEPORT listeners, multishot accept, and the stream-shaped recv/send
/// submits and completions that drive <see cref="Connection"/>. Peer transports: Reactor.Udp.cs /
/// Reactor.Quic.cs.
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
        sqe->user_data = Tag(KindTcpRecv, gen, fd);
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
        sqe->user_data = Tag(KindTcpSend, gen, fd);
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
        sqe->user_data = Tag(KindTcpSend, gen, fd);
    }

    private void SubmitAcceptMultishot(int listenFd)
    {
        IoUringSqe* sqe = GetSqeOrFlush();
        Unsafe.InitBlockUnaligned(sqe, 0, 64);
        sqe->opcode    = IORING_OP_ACCEPT;
        sqe->ioprio    = IORING_ACCEPT_MULTISHOT;
        sqe->fd        = listenFd;
        sqe->user_data = Tag(KindTcpAccept, 0, listenFd);
    }
    
    // Recv completions, one method per loop mode - the single operation the two modes genuinely
    // differ on (where buffers come from and who returns them). The skeleton both share - stale
    // guard, EOF teardown, overflow teardown, re-arm - lives here and in the CloseFromRecv helpers,
    // called from both dispatch switches like the UDP/send completions.

    // Shared mode: one reactor-wide provided-buffer ring; every CQE consumes a whole buffer, so
    // the EOF and stale paths must hand it straight back to the shared pool.
    private void OnTcpRecvCompletionShared(int fd, ushort gen, int res, uint flags)
    {
        bool   hasBuf = (flags & IORING_CQE_F_BUFFER) != 0;
        ushort bid    = hasBuf ? (ushort)(flags >> IORING_CQE_BUFFER_SHIFT) : (ushort)0;

        Connection? conn = ConnAt(fd, gen);

        if (res <= 0)
        {
            // Peer EOF or recv error - reactor owns teardown.
            if (hasBuf)
            {
                ReturnBufferDirect(bid);
            }
            if (conn != null)
            {
                CloseFromRecv(conn, fd);
            }
            return;
        }

        if (conn == null)
        {
            // Stale CQE from the fd's previous tenant.
            if (hasBuf)
            {
                ReturnBufferDirect(bid);
            }
            return;
        }

        byte* ptr = hasBuf ? _bufSlab + (nuint)bid * (nuint)_recvBufferSize : null;
        if (!conn.Complete(res, bid, hasBuf, ptr))
        {
            CloseFromRecvOverflow(conn, fd, gen);
            return;
        }

        if ((flags & IORING_CQE_F_MORE) == 0)
        {
            SubmitRecvMultishot(fd, gen, BgId);
        }
    }

    // Incremental mode: per-connection IOU_PBUF_RING_INC ring - the kernel keeps appending into
    // one bid at a running offset, so instead of returning buffers per CQE this tracks
    // offset/refcount/kernel-done per buffer (the ring is freed wholesale in Recycle).
    private void OnTcpRecvCompletionIncremental(int fd, ushort gen, int res, uint flags)
    {
        bool   more    = (flags & IORING_CQE_F_MORE)     != 0;
        bool   hasBuf  = (flags & IORING_CQE_F_BUFFER)   != 0;
        bool   bufMore = (flags & IORING_CQE_F_BUF_MORE) != 0;
        ushort bid     = hasBuf ? (ushort)(flags >> IORING_CQE_BUFFER_SHIFT) : (ushort)0;

        Connection? conn = ConnAt(fd, gen);

        if (res <= 0)
        {
            // Peer EOF / recv error - the per-conn ring is freed in Recycle.
            if (conn != null)
            {
                CloseFromRecv(conn, fd);
            }
            return;
        }

        if (conn == null)
        {
            return;   // stale CQE; its ring is already gone
        }

        // Data lands at the buffer's running offset; the kernel keeps appending
        // to this bid until the buffer is full (F_BUF_MORE clear).
        byte* ptr = conn.BufSlab + (nuint)bid * (nuint)_incRecvBufferSize + (nuint)conn.CumOffset![bid];
        conn.CumOffset[bid] += res;
        conn.RefCount![bid]++;
        if (!bufMore || !more)
        {
            conn.KernelDone![bid] = true;
        }

        if (!conn.Complete(res, bid, hasBuffer: true, ptr))
        {
            CloseFromRecvOverflow(conn, fd, gen);
            return;
        }

        if (!more)
        {
            SubmitRecvMultishot(fd, gen, conn.Bgid);
        }
    }

    // Accept, both modes: NoDelay, pooled-or-fresh connection, table registration, first recv arm,
    // fault-observed handler launch. The mode branch picks the buffer-ring wiring; _incremental is
    // readonly for the reactor's lifetime, so it predicts perfectly.
    private void OnTcpAcceptCompletion(int listenFd, int res, bool more)
    {
        if (res >= 0)
        {
            int clientFd = res;
            SetNoDelay(clientFd);
            Connection conn = _pool.TryPop(out var pooled)
                ? pooled.SetFd(clientFd)
                : new Connection(this, clientFd, _config.WriteSlabSize, _config.RecvQueueEntries,
                                 _incremental ? WriteOverflowStrategy.Grow : _config.WriteOverflow);
            Track(clientFd, conn);
            conn.InitRefs();
            conn.ListenerPort = PortOf(listenFd);

            if (_incremental)
            {
                SetupConnectionBufRing(conn);
                SubmitRecvMultishot(clientFd, (ushort)conn.Generation, conn.Bgid);
            }
            else
            {
                conn.UseZc = _zeroCopySend;   // config default; kTLS overrides to plain on handshake
                SubmitRecvMultishot(clientFd, (ushort)conn.Generation, BgId);
            }

            _ = RunHandlerAsync(conn);
        }
        else
        {
            Console.Error.WriteLine($"[r{_id}] accept error: {res}");
        }
        if (!more)
        {
            SubmitAcceptMultishot(listenFd);
        }
    }

    // Recv-side teardown, shared by both modes: detach from the table, mark closed, release the
    // recv-side ref.
    private void CloseFromRecv(Connection conn, int fd)
    {
        _connections[fd] = null;
        conn.MarkClosed();
        conn.DecRef();
    }

    // Recv-queue overflow - tear down rather than zombify. The multishot recv is still armed
    // (F_MORE was set), so it is also cancelled by exact user_data.
    private void CloseFromRecvOverflow(Connection conn, int fd, ushort gen)
    {
        _connections[fd] = null;
        SubmitCancel(Tag(KindTcpRecv, gen, fd));
        conn.MarkClosed();
        conn.DecRef();
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