using System.Runtime.CompilerServices;
using static Rhythm.Native;

namespace Rhythm;

/// <summary>
/// One synchronous, single-issuer io_uring reactor = one pinned thread + one ring
/// (SINGLE_ISSUER | DEFER_TASKRUN) + one SO_REUSEPORT listener + an fd-indexed
/// connection table. The thread is the sole issuer: it submits every SQE and
/// processes every CQE, calling the handler inline. No async, no thread pool, no
/// cross-thread queues.
///
/// Per connection the lifecycle is a strict recv↔send alternation:
///   recv completes → DrainAndSend (parse + serialize all ready requests) →
///   one batched send → on send completion → DrainAndSend again (leftover) or
///   re-arm recv. At most one of {recv, send} is in flight per connection, so
///   closing synchronously is always safe.
/// </summary>
internal sealed unsafe class Reactor
{
    private const uint OP_ACCEPT = 1, OP_RECV = 2, OP_SEND = 3;

    private readonly int _id;
    private readonly ushort _port;
    private readonly int _cpu;
    private readonly Dataset _ds;

    private Ring _ring = null!;
    private int _listenFd;
    private readonly Connection?[] _slots = new Connection?[Cfg.MaxFd];
    private readonly Stack<Connection> _pool = new();

    public Reactor(int id, ushort port, int cpu, Dataset ds)
    {
        _id = id; _port = port; _cpu = cpu; _ds = ds;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Ud(uint op, int fd) => ((ulong)op << 32) | (uint)fd;

    private IoUringSqe* Sqe()
    {
        IoUringSqe* sqe = _ring.GetSqe();
        if (sqe == null) { _ring.SubmitAndWait(0); sqe = _ring.GetSqe(); }
        Unsafe.InitBlockUnaligned(sqe, 0, 64);
        return sqe;
    }

    public void Run()
    {
        Affinity.Pin(_cpu);
        _listenFd = MakeListener(_port);
        _ring = Ring.Create(Cfg.RingEntries);
        Console.WriteLine($"[rhythm r{_id}] listening on :{_port} (cpu {_cpu})");
        ArmAccept();

        while (true)
        {
            _ring.SubmitAndWait(1);
            uint n = _ring.CqReady();
            for (uint i = 0; i < n; i++)
            {
                ref readonly IoUringCqe cqe = ref _ring.CqeAt(i);
                Dispatch(cqe.user_data, cqe.res, cqe.flags);
            }
            _ring.CqAdvance(n);
        }
    }

    private void Dispatch(ulong ud, int res, uint flags)
    {
        switch ((uint)(ud >> 32))
        {
            case OP_ACCEPT: OnAccept(res, flags); break;
            case OP_RECV: OnRecv((int)(ud & 0xffffffff), res); break;
            case OP_SEND: OnSend((int)(ud & 0xffffffff), res); break;
        }
    }

    private void OnAccept(int res, uint flags)
    {
        if (res >= 0)
        {
            int cfd = res;
            if (cfd < Cfg.MaxFd)
            {
                int one = 1;
                setsockopt(cfd, IPPROTO_TCP, TCP_NODELAY, &one, sizeof(int));
                Connection c = _pool.Count > 0 ? _pool.Pop() : new Connection();
                c.Reset(cfd);
                _slots[cfd] = c;
                ArmRecv(c);
            }
            else { close(cfd); }
        }
        if ((flags & IORING_CQE_F_MORE) == 0) ArmAccept(); // re-arm if multishot ended
    }

    private void OnRecv(int fd, int res)
    {
        Connection? c = _slots[fd];
        if (c == null) return;
        if (res <= 0) { Close(c); return; }
        c.RecvLen += res;
        try { DrainAndSend(c); }
        catch { Close(c); }
    }

    private void OnSend(int fd, int res)
    {
        Connection? c = _slots[fd];
        if (c == null) return;
        if (res <= 0) { Close(c); return; }
        c.WriteSent += res;
        if (c.WriteSent < c.WriteLen) { SubmitSend(c); return; } // partial send
        c.WriteLen = 0;
        c.WriteSent = 0;
        if (c.CloseAfter) { Close(c); return; }
        try { DrainAndSend(c); }
        catch { Close(c); }
    }

    /// Parse every complete request currently buffered, serialize each response
    /// into the write buffer, then submit one batched send — or re-arm recv if
    /// nothing is ready. This is the only place that touches the handler.
    private void DrainAndSend(Connection c)
    {
        int off = 0;
        bool close = false;
        while (off < c.RecvLen)
        {
            var recv = new ReadOnlySpan<byte>(c.Recv + off, c.RecvLen - off);
            var write = new Span<byte>(c.Write + c.WriteLen, Cfg.WriteBuf - c.WriteLen);
            int consumed = Http.Process(recv, write, _ds, out int wrote, out bool reqClose);
            if (consumed == 0) break;                  // incomplete
            if (consumed < 0) { close = true; break; } // error / no write room
            c.WriteLen += wrote;
            off += consumed;
            if (reqClose) { close = true; break; }
        }

        if (off > 0)
        {
            int rem = c.RecvLen - off;
            if (rem > 0) Buffer.MemoryCopy(c.Recv + off, c.Recv, Cfg.RecvBuf, rem);
            c.RecvLen = rem;
        }

        if (c.WriteLen > 0) { c.CloseAfter = close; SubmitSend(c); }
        else if (close) Close(c);
        else if (c.RecvLen >= Cfg.RecvBuf) Close(c); // request larger than the buffer
        else ArmRecv(c);
    }

    // ── io_uring submitters ──────────────────────────────────────────────────
    private void ArmAccept()
    {
        IoUringSqe* sqe = Sqe();
        sqe->opcode = IORING_OP_ACCEPT;
        sqe->ioprio = IORING_ACCEPT_MULTISHOT;
        sqe->fd = _listenFd;
        sqe->user_data = Ud(OP_ACCEPT, _listenFd);
    }

    private void ArmRecv(Connection c)
    {
        IoUringSqe* sqe = Sqe();
        sqe->opcode = IORING_OP_RECV;
        sqe->fd = c.Fd;
        sqe->addr = (ulong)(c.Recv + c.RecvLen);
        sqe->len = (uint)(Cfg.RecvBuf - c.RecvLen);
        sqe->user_data = Ud(OP_RECV, c.Fd);
    }

    private void SubmitSend(Connection c)
    {
        IoUringSqe* sqe = Sqe();
        sqe->opcode = IORING_OP_SEND;
        sqe->fd = c.Fd;
        sqe->addr = (ulong)(c.Write + c.WriteSent);
        sqe->len = (uint)(c.WriteLen - c.WriteSent);
        sqe->op_flags = Cfg.MsgNoSignal;
        sqe->user_data = Ud(OP_SEND, c.Fd);
    }

    private void Close(Connection c)
    {
        int fd = c.Fd;
        close(fd);
        _slots[fd] = null;
        if (_pool.Count < Cfg.PoolMax) _pool.Push(c); else c.FreeNative();
    }

    private static int MakeListener(ushort port)
    {
        int fd = socket(AF_INET, SOCK_STREAM, 0);
        int one = 1;
        setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &one, sizeof(int));
        setsockopt(fd, SOL_SOCKET, SO_REUSEPORT, &one, sizeof(int));
        sockaddr_in addr = default;
        addr.sin_family = AF_INET;
        addr.sin_port = Htons(port);
        addr.sin_addr.s_addr = 0;
        if (bind(fd, &addr, (uint)sizeof(sockaddr_in)) < 0) throw new InvalidOperationException("bind failed");
        if (listen(fd, 1024) < 0) throw new InvalidOperationException("listen failed");
        return fd;
    }
}
