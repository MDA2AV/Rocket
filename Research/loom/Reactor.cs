using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using static Loom.Native;

namespace Loom;

/// <summary>
/// One io_uring reactor per core (SO_REUSEPORT, SINGLE_ISSUER|DEFER_TASKRUN). It is the
/// single issuer of its ring, and it installs a <see cref="LoomSyncContext"/> on its own
/// thread so that every async continuation in a handler comes back here.
///
/// Loop = reap CQEs (accept / recv / send / wake) + run any posted continuations. Handlers
/// run inline on this thread; when one awaits off-thread work, the work runs elsewhere but
/// its continuation is posted back (eventfd wake) and resumed here — then it can submit the
/// response SEND directly, because it's on the issuer thread again.
/// </summary>
internal sealed unsafe class Reactor
{
    private const uint OP_ACCEPT = 1, OP_RECV = 2, OP_SEND = 3, OP_WAKE = 4, OP_RING = 5, OP_DB = 6;
    private const byte IORING_OP_NOP = 0;
    private const byte IORING_OP_TIMEOUT = 11;
    private const uint RING_ENTRIES = 4096;
    private const int MAX_FD = 1 << 16;

    private readonly int _id;
    private readonly ushort _port;
    private Ring _ring = null!;
    private int _listenFd;
    private int _wakeFd;
    private int _threadId;
    private readonly Connection?[] _slots = new Connection?[MAX_FD];
    private readonly Stack<Connection> _pool = new();
    private readonly ConcurrentQueue<(SendOrPostCallback cb, object? st)> _continuations = new();
    private DbConn _db = null!;     // this reactor's Postgres connection (its SEND/RECV ride this ring)

    public Reactor(int id, ushort port) { _id = id; _port = port; }

    public bool OnReactorThread => Environment.CurrentManagedThreadId == _threadId;

    /// <summary>SyncContext entry point: queue a continuation and (if off-thread) wake the reactor.</summary>
    public void Post(SendOrPostCallback cb, object? state)
    {
        _continuations.Enqueue((cb, state));
        if (!OnReactorThread)
        {
            ulong one = 1;
            write(_wakeFd, &one, 8);
        }
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
        _threadId = Environment.CurrentManagedThreadId;
        SynchronizationContext.SetSynchronizationContext(new LoomSyncContext(this));
        _listenFd = MakeListener(_port);
        _wakeFd = eventfd(0, EFD_NONBLOCK | EFD_CLOEXEC);
        _ring = Ring.Create(RING_ENTRIES);
        if (Http.UseDb) _db = Pg.Connect("bench", "bench");   // one-time blocking handshake; queries ride the ring
        Console.WriteLine($"[loom r{_id}] listening on :{_port}{(Http.UseDb ? " (+pg on ring)" : "")}");
        ArmAccept();
        ArmWake();

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

            // Run continuations marshalled back by the SyncContext (Task.Run/DB completions,
            // or anything posted inline). They execute ON THIS THREAD, so they may submit SQEs.
            while (_continuations.TryDequeue(out var c))
                c.cb(c.st);
        }
    }

    private void Dispatch(ulong ud, int res, uint flags)
    {
        switch ((uint)(ud >> 32))
        {
            case OP_ACCEPT: OnAccept(res, flags); break;
            case OP_RECV: OnRecv((int)(ud & 0xffffffff), res); break;
            case OP_SEND: OnSend((int)(ud & 0xffffffff), res); break;
            case OP_WAKE: OnWake(); break;
            case OP_RING: OnRing((int)(ud & 0xffffffff), res); break;
            case OP_DB: _db.IoComplete(res); break;   // DB SEND/RECV CQE → resume the query inline
        }
    }

    // ── Postgres over the ring ───────────────────────────────────────────────
    // The query SEND and the response RECV are ordinary ring ops on the DB socket fd; their
    // CQEs complete the DbConn's IVTS (RCA=false), so the await resumes inline on the reactor —
    // no .NET socket engine, no thread pool, no marshal-home. The async orchestration lives in
    // Http.DbQuery (this class is `unsafe`, so it can't hold an `await`); these are its sync
    // (unsafe) primitives, all run on the reactor thread.

    internal int DbPrepareQuery(string sql) => Pg.Query(_db.Send, sql);
    internal void DbResetRecv() => _db.RecvLen = 0;

    internal bool DbFinishRecv(int n, out string result)
    {
        _db.RecvLen += n;
        if (Pg.TryParse(new ReadOnlySpan<byte>(_db.Recv, _db.RecvLen), out int fs, out int fl, out bool ready) && ready)
        {
            result = fl > 0 ? System.Text.Encoding.ASCII.GetString(_db.Recv + fs, fl) : "";
            return true;
        }
        result = "";
        return false;
    }

    internal ValueTask<int> DbSendAsync(int len)
    {
        ValueTask<int> vt = _db.IoAwait();
        IoUringSqe* s = Sqe();
        s->opcode = IORING_OP_SEND;
        s->fd = _db.Fd;
        s->addr = (ulong)_db.Send;
        s->len = (uint)len;
        s->op_flags = 0x4000;   // MSG_NOSIGNAL
        s->user_data = Ud(OP_DB, _db.Fd);
        return vt;
    }

    internal ValueTask<int> DbRecvAsync()
    {
        ValueTask<int> vt = _db.IoAwait();
        IoUringSqe* s = Sqe();
        s->opcode = IORING_OP_RECV;
        s->fd = _db.Fd;
        s->addr = (ulong)(_db.Recv + _db.RecvLen);
        s->len = (uint)(DbConn.Buf - _db.RecvLen);
        s->user_data = Ud(OP_DB, _db.Fd);
        return vt;
    }

    /// <summary>
    /// Submit an io_uring NOP and return an awaitable completed by its CQE — the whole "async"
    /// op lives on the ring, on THIS thread, with no thread pool. (A real async-I/O version
    /// would submit a file/DB-socket read instead of NOP; same mechanism.)
    /// </summary>
    public ValueTask<int> RingYieldAsync(Connection conn)
    {
        ValueTask<int> vt = conn.RingAwait();   // reset IVTS + capture version BEFORE submit
        IoUringSqe* s = Sqe();
        s->opcode = IORING_OP_NOP;
        s->user_data = Ud(OP_RING, conn.Fd);
        return vt;
    }

    private void OnRing(int fd, int res)
    {
        Connection? c = _slots[fd];
        c?.RingComplete(res);   // RCA=false ⇒ resumes the awaiting handler inline on this thread
    }

    /// <summary>
    /// Async delay via io_uring TIMEOUT — real latency, served entirely on the reactor (the
    /// thread stays free for other connections while this one waits). No thread pool. This is
    /// the shape of any async I/O on your ring: replace TIMEOUT with a file/DB read.
    /// </summary>
    public ValueTask<int> DelayAsync(Connection conn, long micros)
    {
        long* ts = (long*)conn.Ts;
        ts[0] = micros / 1_000_000;            // tv_sec
        ts[1] = (micros % 1_000_000) * 1000;   // tv_nsec
        ValueTask<int> vt = conn.RingAwait();
        IoUringSqe* s = Sqe();
        s->opcode = IORING_OP_TIMEOUT;
        s->addr = (ulong)conn.Ts;
        s->len = 1;                            // one timespec
        s->off = 0;                            // pure time-based (fire after the duration)
        s->user_data = Ud(OP_RING, conn.Fd);
        return vt;
    }

    private void OnWake()
    {
        ulong drain;
        read(_wakeFd, &drain, 8);   // clear the counter; continuations are drained in the loop
    }

    private void OnAccept(int res, uint flags)
    {
        if (res >= 0)
        {
            int cfd = res;
            if (cfd < MAX_FD)
            {
                int one = 1;
                setsockopt(cfd, IPPROTO_TCP, TCP_NODELAY, &one, sizeof(int));
                Connection c = _pool.Count > 0 ? _pool.Pop() : new Connection();
                c.Reset(cfd);
                _slots[cfd] = c;
                ArmRecv(c);
            }
            else close(cfd);
        }
        if ((flags & IORING_CQE_F_MORE) == 0) ArmAccept();
    }

    private void OnRecv(int fd, int res)
    {
        Connection? c = _slots[fd];
        if (c == null) return;
        if (res <= 0) { Close(c); return; }
        c.RecvLen += res;
        // Run the async handler inline on the reactor thread. If it awaits, it suspends and
        // its continuation comes back via Post; otherwise it runs straight through to Send.
        _ = Http.Handle(this, c);
    }

    private void OnSend(int fd, int res)
    {
        Connection? c = _slots[fd];
        if (c == null) return;
        if (res <= 0) { Close(c); return; }
        c.WriteSent += res;
        if (c.WriteSent < c.WriteLen) { SubmitSend(c); return; }
        c.WriteLen = 0; c.WriteSent = 0;
        if (c.CloseAfter) { Close(c); return; }
        ArmRecv(c);
    }

    // Called by a handler (on the reactor thread) to send its response.
    public void Send(Connection c) => SubmitSend(c);

    // ── io_uring submitters ──────────────────────────────────────────────────
    private void ArmAccept()
    {
        IoUringSqe* s = Sqe();
        s->opcode = IORING_OP_ACCEPT;
        s->ioprio = IORING_ACCEPT_MULTISHOT;
        s->fd = _listenFd;
        s->user_data = Ud(OP_ACCEPT, _listenFd);
    }

    public void ArmRecv(Connection c)
    {
        IoUringSqe* s = Sqe();
        s->opcode = IORING_OP_RECV;
        s->fd = c.Fd;
        s->addr = (ulong)(c.Recv + c.RecvLen);
        s->len = (uint)(Connection.RecvBuf - c.RecvLen);
        s->user_data = Ud(OP_RECV, c.Fd);
    }

    private void SubmitSend(Connection c)
    {
        IoUringSqe* s = Sqe();
        s->opcode = IORING_OP_SEND;
        s->fd = c.Fd;
        s->addr = (ulong)(c.Write + c.WriteSent);
        s->len = (uint)(c.WriteLen - c.WriteSent);
        s->op_flags = 0x4000;   // MSG_NOSIGNAL
        s->user_data = Ud(OP_SEND, c.Fd);
    }

    private void ArmWake()
    {
        IoUringSqe* s = Sqe();
        s->opcode = IORING_OP_POLL_ADD;
        s->fd = _wakeFd;
        s->op_flags = POLLIN;                 // poll32_events at this offset
        s->len = IORING_POLL_ADD_MULTI;       // multishot — stays armed
        s->user_data = Ud(OP_WAKE, _wakeFd);
    }

    private void Close(Connection c)
    {
        int fd = c.Fd;
        close(fd);
        _slots[fd] = null;
        if (_pool.Count < 4096) _pool.Push(c); else c.FreeNative();
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
