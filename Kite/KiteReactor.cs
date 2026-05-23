namespace Kite;

/// <summary>
/// Lean io_uring reactor: one thread + one ring + one SO_REUSEPORT listener +
/// a shared provided-buffer ring. Multishot accept + multishot recv; recv bytes
/// are copied into the connection's Input pipe. The ring does NOT do the send —
/// the response goes out via libc send() in the connection's pump, exactly like
/// KestrelShrike. This isolates "io_uring recv loop" vs "epoll recv loop" with
/// everything else (lean connection, dual pipes, libc send) held identical.
/// </summary>
internal sealed unsafe class KiteReactor
{
    public readonly int Id;
    private readonly ushort _port;
    private readonly uint _ringEntries;
    private readonly uint _recvBufferSize;
    private readonly uint _bufferRingEntries;

    private Ring _ring = null!;
    private int _listenFd;
    private readonly ConcurrentDictionary<int, KiteConnection> _conns = new();
    private volatile bool _running = true;

    internal Action<KiteConnection>? OnAccept;

    // CQE user_data: kind tag in the high 32 bits, fd in the low 32.
    private const ulong KindAccept = 1UL << 32;
    private const ulong KindRecv   = 2UL << 32;
    private const ushort BgId = 1;

    // Shared provided-buffer ring (one per reactor).
    private byte*  _bufRing;
    private byte*  _bufSlab;
    private uint   _bufRingMask;
    private ushort _bufRingTail;

    private const int EINTR = 4;
    private const int EAGAIN = 11;
    private const int EBUSY = 16;

    public KiteReactor(int id, ushort port, uint ringEntries, int recvBufferSize, int bufferRingEntries)
    {
        Id = id;
        _port = port;
        _ringEntries = ringEntries;
        _recvBufferSize = (uint)recvBufferSize;
        _bufferRingEntries = (uint)bufferRingEntries;
    }

    public void Stop() => _running = false;

    internal void Remove(KiteConnection conn)
        => _conns.TryRemove(new KeyValuePair<int, KiteConnection>(conn.Fd, conn));

    public void Run()
    {
        _ring = Ring.Create(_ringEntries);
        _listenFd = OpenReusePortListener(_port);
        InitBufferRing();

        Console.WriteLine($"[kite r{Id}] listening on 0.0.0.0:{_port}");
        SubmitAcceptMultishot();

        while (_running)
        {
            int rc = _ring.SubmitAndWait(1);
            if (rc < 0 && rc != -EINTR && rc != -EAGAIN && rc != -EBUSY)
            {
                Console.Error.WriteLine($"[kite r{Id}] io_uring_enter failed: {rc}");
                break;
            }

            uint ready = _ring.CqReady();
            for (uint i = 0; i < ready; i++)
            {
                Dispatch(in _ring.CqeAt(i));
            }
            _ring.CqAdvance(ready);
        }

        close(_listenFd);
        _ring.Dispose();
    }

    private void Dispatch(in IoUringCqe cqe)
    {
        ulong kind = cqe.user_data & 0xffffffff_00000000UL;
        int   fd   = (int)(cqe.user_data & 0xffffffffUL);
        bool  more = (cqe.flags & IORING_CQE_F_MORE) != 0;

        if (kind == KindAccept)
        {
            if (cqe.res >= 0)
            {
                int clientFd = cqe.res;
                SetNoDelay(clientFd);
                var conn = new KiteConnection(clientFd, this);
                _conns[clientFd] = conn;
                SubmitRecvMultishot(clientFd);
                OnAccept?.Invoke(conn);     // vend to the Kestrel transport
                _ = conn.RunOutputPump();   // start the libc-send pump (thread pool)
            }
            else
            {
                Console.Error.WriteLine($"[kite r{Id}] accept error: {cqe.res}");
            }
            if (!more) SubmitAcceptMultishot();
            return;
        }

        if (kind == KindRecv)
        {
            bool   hasBuf = (cqe.flags & IORING_CQE_F_BUFFER) != 0;
            ushort bid    = hasBuf ? (ushort)(cqe.flags >> IORING_CQE_BUFFER_SHIFT) : (ushort)0;

            if (cqe.res <= 0)
            {
                // Peer EOF / recv error / cancel (e.g. shutdown from Kestrel close).
                if (hasBuf) ReturnBufferDirect(bid);
                if (_conns.TryRemove(fd, out var dying))
                {
                    dying.MarkClosed();
                    dying.DecRef();   // release the reactor's recv-side ref
                }
                return;
            }

            if (hasBuf && _conns.TryGetValue(fd, out var conn))
            {
                conn.OnRecv(_bufSlab + (nuint)bid * (nuint)_recvBufferSize, cqe.res);
                ReturnBufferDirect(bid);
            }
            else if (hasBuf)
            {
                ReturnBufferDirect(bid);
            }

            if (!more) SubmitRecvMultishot(fd);
        }
    }

    // =========================================================================
    // Provided-buffer ring (copied from Spring's reactor)
    // =========================================================================

    private void InitBufferRing()
    {
        nuint ringBytes = (nuint)_bufferRingEntries * 16;
        _bufRing = (byte*)NativeMemory.AlignedAlloc(ringBytes, 4096);
        NativeMemory.Clear(_bufRing, ringBytes);

        nuint slabBytes = _bufferRingEntries * (nuint)_recvBufferSize;
        _bufSlab = (byte*)NativeMemory.AlignedAlloc(slabBytes, 64);

        _bufRingMask = _bufferRingEntries - 1;

        var reg = new io_uring_buf_reg
        {
            ring_addr    = (ulong)_bufRing,
            ring_entries = _bufferRingEntries,
            bgid         = BgId,
        };

        int ret = io_uring_register(_ring.Fd, IORING_REGISTER_PBUF_RING, &reg, 1);
        if (ret < 0)
        {
            int err = Marshal.GetLastPInvokeError();
            throw new InvalidOperationException($"register pbuf_ring failed: ret={ret} errno={err}");
        }

        for (ushort bid = 0; bid < _bufferRingEntries; bid++)
        {
            byte* slot = _bufRing + (uint)bid * 16;
            *(ulong*)(slot + 0)   = (ulong)(_bufSlab + bid * (nuint)_recvBufferSize);
            *(uint*)(slot + 8)    = _recvBufferSize;
            *(ushort*)(slot + 12) = bid;
        }
        _bufRingTail = (ushort)_bufferRingEntries;
        Volatile.Write(ref *(ushort*)(_bufRing + 14), _bufRingTail);
    }

    private void ReturnBufferDirect(ushort bid)
    {
        byte* slot = _bufRing + (_bufRingTail & _bufRingMask) * 16;
        *(ulong*)(slot + 0)   = (ulong)(_bufSlab + bid * (nuint)_recvBufferSize);
        *(uint*)(slot + 8)    = _recvBufferSize;
        *(ushort*)(slot + 12) = bid;
        _bufRingTail++;
        Volatile.Write(ref *(ushort*)(_bufRing + 14), _bufRingTail);
    }

    // =========================================================================
    // SQE producers (reactor-thread only)
    // =========================================================================

    private IoUringSqe* GetSqeOrFlush()
    {
        IoUringSqe* sqe = _ring.GetSqe();
        if (sqe != null) return sqe;

        _ring.SubmitAndWait(0);
        sqe = _ring.GetSqe();
        if (sqe == null) throw new InvalidOperationException("SQ full after flush");
        return sqe;
    }

    private void SubmitAcceptMultishot()
    {
        IoUringSqe* sqe = GetSqeOrFlush();
        Unsafe.InitBlockUnaligned(sqe, 0, 64);
        sqe->opcode    = IORING_OP_ACCEPT;
        sqe->ioprio    = IORING_ACCEPT_MULTISHOT;
        sqe->fd        = _listenFd;
        sqe->user_data = KindAccept | (uint)_listenFd;
    }

    private void SubmitRecvMultishot(int fd)
    {
        IoUringSqe* sqe = GetSqeOrFlush();
        Unsafe.InitBlockUnaligned(sqe, 0, 64);
        sqe->opcode    = IORING_OP_RECV;
        sqe->flags     = IOSQE_BUFFER_SELECT;
        sqe->ioprio    = IORING_RECV_MULTISHOT;
        sqe->fd        = fd;
        sqe->buf_index = BgId;
        sqe->user_data = KindRecv | (uint)fd;
    }

    private static void SetNoDelay(int fd)
    {
        int one = 1;
        setsockopt(fd, IPPROTO_TCP, TCP_NODELAY, &one, sizeof(int));
    }

    private static int OpenReusePortListener(ushort port)
    {
        int fd = socket(AF_INET, SOCK_STREAM, 0);
        if (fd < 0) throw new InvalidOperationException($"socket failed: {fd}");

        int one = 1;
        setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &one, sizeof(int));
        setsockopt(fd, SOL_SOCKET, SO_REUSEPORT, &one, sizeof(int));

        sockaddr_in addr = default;
        addr.sin_family      = AF_INET;
        addr.sin_port        = Htons(port);
        addr.sin_addr.s_addr = 0; // 0.0.0.0

        if (bind(fd, &addr, (uint)sizeof(sockaddr_in)) < 0) throw new InvalidOperationException("bind failed");
        if (listen(fd, 128) < 0) throw new InvalidOperationException("listen failed");
        return fd;
    }
}
