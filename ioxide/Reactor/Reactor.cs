using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ioxide.utils;
using static ioxide.Native;
// ReSharper disable SuggestVarOrType_BuiltInTypes

namespace ioxide;

/// <summary>
/// One reactor = one thread + one io_uring + one SO_REUSEPORT listener + one connection table.
/// The reactor thread is the sole writer of the SQ, the buf_ring, and the connection table;
/// off-reactor handlers reach it through MPSC queues woken by an eventfd poll.
/// </summary>
public sealed unsafe partial class Reactor
{
    public readonly int Id;
    public Ring Ring = null!;   // created on the reactor thread (DEFER_TASKRUN requires same-thread setup+enter)

    // Connection table indexed by fd (dense small ints - array beats Dictionary per CQE).
    private Connection?[] _connections = new Connection?[4096];

    private int[] _listenFds = [];
    private ushort[] _listenPorts = [];
    private readonly ServerConfig _config;

    // The response-send strategy from config (ZeroCopySend), copied per-connection at accept into
    // Connection.UseZc. The send hot path branches on that bool (predictable, inlinable) instead of
    // dispatching through an indirect function pointer.
    private readonly bool _zeroCopySend;
    private readonly ushort _port;
    private readonly uint _ringEntries;
    private readonly bool _incremental;
    private readonly uint RecvBufferSize;

    // user_data: [63:56] kind | [47:32] connection generation | [31:0] fd (or client-op slot).
    // The generation makes straggler CQEs from a reused fd detectable as stale.
    private const int  KindShift  = 56;
    private const int  GenShift   = 32;
    private const byte KindAccept = 1;
    private const byte KindRecv   = 2;
    private const byte KindSend   = 3;
    private const byte KindWake   = 4;
    private const byte KindClient = 5;   // low 32 bits = op slot (Reactor.RingHost.cs)
    private const byte KindCancel = 6;
    private const byte KindTimer  = 7;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Tag(byte kind, ushort gen, int fd)
        => ((ulong)kind << KindShift) | ((ulong)gen << GenShift) | (uint)fd;

    // Shared provided-buffer ring (one per reactor).
    private const ushort BgId = 1;
    private readonly uint BufferRingEntries;   // power of two
    private byte*  _bufRing;                   // io_uring_buf_ring (kernel-shared)
    private byte*  _bufSlab;
    private uint   _bufRingMask;
    private ushort _bufRingTail;

    // Off-reactor handoff queues + eventfd wake. Reactor-thread callers take the
    // direct fast path instead (no queue, no syscall).
    private int _wakeFd;
    private int _reactorThreadId;

    // Set cross-thread by Stop(); the loops check it at the top of each iteration and exit, after which
    // Run() tears the ring down on this (the reactor) thread - mandatory for a single-issuer ring.
    private volatile bool _stopRequested;

    // Periodic timer driving registered tickers (per-command timeout sweeps, pool replenishment).
    // Single-shot, re-armed each fire. One timer in flight per reactor.
    private __kernel_timespec* _timerTs;
    private const long TimerIntervalNs = 250_000_000;   // 250 ms
    private readonly List<Action> _tickers = new();

    private readonly Mpsc<ushort> _returnQ = new(1 << 14);
    private readonly Mpsc<ulong>  _flushQ  = new(1 << 12);   // (gen << 32) | fd

    // Recycle must run on the reactor (buf_ring + pool are reactor-owned). Connection is a
    // ref type, so this queue is a ConcurrentQueue rather than the unmanaged Mpsc<T>.
    private readonly ConcurrentQueue<Connection> _recycleQ = new();

    // Connection pool, reactor-thread-only. PoolMax × WriteSlabSize × ReactorCount bounds
    // the reserved native memory.
    private readonly int PoolMax;
    private readonly Stack<Connection> _pool;

    // Per-reactor pool of base-size write slabs, rented by connections in Segmented overflow mode.
    // Reactor-thread-only, so no locking. Capped so a burst of large responses doesn't retain memory.
    private readonly Stack<nint> _writeSlabPool = new();
    private const int WriteSlabPoolMax = 4096;

    internal nint RentWriteSlab()
        => _writeSlabPool.TryPop(out nint p) ? p : (nint)NativeMemory.AlignedAlloc((nuint)_config.WriteSlabSize, 64);

    internal void ReturnWriteSlab(nint p)
    {
        if (_writeSlabPool.Count < WriteSlabPoolMax)
        {
            _writeSlabPool.Push(p);
        }
        else
        {
            NativeMemory.AlignedFree((void*)p);
        }
    }

    // Incremental-mode sizing (see Reactor.Incremental.cs).
    private readonly int  MaxConnections;       // one bgid per active connection
    private readonly int  ConnBufRingEntries;
    private readonly uint IncRecvBufferSize;

    // Transient io_uring_enter errnos.
    private const int EINTR  = 4;
    private const int EAGAIN = 11;
    private const int EBUSY  = 16;

    public Reactor(int id, ServerConfig config)
    {
        Id = id;
        _config = config;
        _port = config.Port;
        _ringEntries = config.RingEntries;
        _incremental = config.Incremental;
        RecvBufferSize = (uint)config.RecvBufferSize;
        BufferRingEntries = (uint)config.BufferRingEntries;
        PoolMax = config.PoolMax;
        MaxConnections = config.MaxConnections;
        ConnBufRingEntries = config.ConnBufRingEntries;
        IncRecvBufferSize = (uint)config.IncRecvBufferSize;
        _pool = new Stack<Connection>(config.PoolMax);
        _zeroCopySend = config.ZeroCopySend;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Connection? ConnAt(int fd, ushort gen)
    {
        Connection?[] conns = _connections;
        Connection? conn = (uint)fd < (uint)conns.Length ? conns[fd] : null;
        return conn != null && (ushort)conn.Generation == gen ? conn : null;
    }

    private void Track(int fd, Connection conn)
    {
        if (fd >= _connections.Length)
        {
            int newLength = _connections.Length;
            while (newLength <= fd)
            {
                newLength *= 2;
            }
            Array.Resize(ref _connections, newLength);
        }
        _connections[fd] = conn;
    }

    private void InitBufferRing()
    {
        nuint ringBytes = (nuint)BufferRingEntries * 16;
        _bufRing = (byte*)NativeMemory.AlignedAlloc(ringBytes, 4096);
        NativeMemory.Clear(_bufRing, ringBytes);

        nuint slabBytes = BufferRingEntries * (nuint)RecvBufferSize;
        _bufSlab = (byte*)NativeMemory.AlignedAlloc(slabBytes, 64);

        _bufRingMask = BufferRingEntries - 1;

        var reg = new io_uring_buf_reg {
            ring_addr    = (ulong)_bufRing,
            ring_entries = BufferRingEntries,
            bgid         = BgId,
        };

        int ret = io_uring_register(Ring.Fd, IORING_REGISTER_PBUF_RING, &reg, 1);
        if (ret < 0)
        {
            int err = Marshal.GetLastPInvokeError();

            throw new InvalidOperationException($"register pbuf_ring failed: ret={ret} errno={err}");
        }

        // Slot 0 overlaps the ring's tail field at offset 14; writing only addr/len/bid
        // (offsets 0..13) keeps tail zero until published explicitly.
        for (ushort bid = 0; bid < BufferRingEntries; bid++) {
            byte* slot = _bufRing + (uint)bid * 16;
            *(ulong*)(slot + 0)  = (ulong)(_bufSlab + bid * (nuint)RecvBufferSize);
            *(uint*)(slot + 8)   = RecvBufferSize;
            *(ushort*)(slot + 12) = bid;
        }
        _bufRingTail = (ushort)BufferRingEntries;

        PublishBufRingTail();
    }

    // Stage a buffer without publishing; batch drains publish once for N buffers.
    private void ReturnBufferLocal(ushort bid)
    {
        byte* slot = _bufRing + (_bufRingTail & _bufRingMask) * 16;
        *(ulong*)(slot + 0)  = (ulong)(_bufSlab + bid * (nuint)RecvBufferSize);
        *(uint*)(slot + 8)   = RecvBufferSize;
        *(ushort*)(slot + 12) = bid;
        _bufRingTail++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void PublishBufRingTail()
    {
        Volatile.Write(ref *(ushort*)(_bufRing + 14), _bufRingTail);
    }

    // Reactor-thread-only; off-reactor callers use EnqueueReturnQ.
    internal void ReturnBufferDirect(ushort bid)
    {
        ReturnBufferLocal(bid);
        PublishBufRingTail();
    }

    // Safe from any thread.
    public void EnqueueReturnQ(ushort bid)
    {
        if (Environment.CurrentManagedThreadId == _reactorThreadId)
        {
            ReturnBufferDirect(bid);
            return;
        }
        SpinWait sw = default;
        while (!_returnQ.TryEnqueue(bid))
        {
            sw.SpinOnce();
        }
        // Without the wake, a queued return waits for an unrelated CQE; if the ring
        // drains meanwhile, recvs fail with ENOBUFS.
        WakeFdWrite();
    }

    internal void EnqueueFlush(int fd, int gen)
    {
        if (Environment.CurrentManagedThreadId == _reactorThreadId)
        {
            Connection? conn = ConnAt(fd, (ushort)gen);
            if (conn != null)
            {
                SubmitFlush(conn, fd, (ushort)gen);
            }
            return;
        }
        ulong packed = ((ulong)(ushort)gen << 32) | (uint)fd;
        SpinWait sw = default;
        while (!_flushQ.TryEnqueue(packed))
        {
            sw.SpinOnce();
        }
        WakeFdWrite();
    }

    // Called by Connection.DecRef at refcount 0.
    internal void EnqueueRecycle(Connection conn)
    {
        if (Environment.CurrentManagedThreadId == _reactorThreadId)
        {
            Recycle(conn, conn.ClientFd);
            return;
        }
        _recycleQ.Enqueue(conn);
        WakeFdWrite();
    }

    private void WakeFdWrite()
    {
        ulong v = 1;
        write(_wakeFd, &v, 8);   // eventfd becomes readable → multishot poll CQE wakes the loop
    }

    private void DrainReturnQ()
    {
        bool any = false;
        while (_returnQ.TryDequeue(out ushort bid))
        {
            ReturnBufferLocal(bid);
            any = true;
        }
        if (any)
        {
            PublishBufRingTail();
        }
    }

    private void DrainFlushQ()
    {
        while (_flushQ.TryDequeue(out ulong packed))
        {
            int    fd  = (int)(uint)packed;
            ushort gen = (ushort)(packed >> 32);
            // Gen check drops flushes for connections that closed (or whose fd was reused)
            // after queuing.
            Connection? conn = ConnAt(fd, gen);
            if (conn == null)
            {
                continue;
            }
            SubmitFlush(conn, fd, gen);
        }
    }

    private void DrainRecycleQ()
    {
        while (_recycleQ.TryDequeue(out Connection? conn))
        {
            Recycle(conn, conn.ClientFd);
        }
    }

    private void ArmWakePoll()
    {
        IoUringSqe* sqe = GetSqeOrFlush();
        Unsafe.InitBlockUnaligned(sqe, 0, 64);
        sqe->opcode    = IORING_OP_POLL_ADD;
        sqe->fd        = _wakeFd;
        sqe->op_flags  = POLLIN;                  // poll32_events
        sqe->len       = IORING_POLL_ADD_MULTI;
        sqe->user_data = Tag(KindWake, 0, _wakeFd);
    }

    /// <summary>
    /// Requests the reactor to stop. Safe to call from any thread. The loop finishes its current
    /// iteration and exits, then <see cref="Run"/> closes the listeners and wake fd and disposes the
    /// io_uring ring on the reactor thread (a single-issuer / DEFER_TASKRUN ring must be torn down on the
    /// thread that owns it). Join the reactor thread after calling this to await teardown.
    /// </summary>
    public void Stop()
    {
        _stopRequested = true;

        // Wake a loop parked in io_uring_enter so it observes the flag promptly. Writing the eventfd is
        // the only ring-adjacent action safe off the reactor thread. The guard covers Stop() racing ahead
        // of Run() creating _wakeFd - the loop still sees the flag on its first iteration.
        if (_wakeFd > 0)
        {
            WakeFdWrite();
        }
    }

    public void Run()
    {
        _reactorThreadId = Environment.CurrentManagedThreadId;

        Ring = Ring.Create(_ringEntries);

        // One SO_REUSEPORT listener per port; accepts route by listener fd.
        _listenFds = new int[1 + _config.ExtraPorts.Length];
        _listenPorts = new ushort[_listenFds.Length];
        _listenPorts[0] = _port;
        for (int i = 0; i < _config.ExtraPorts.Length; i++)
        {
            _listenPorts[i + 1] = _config.ExtraPorts[i];
        }
        for (int i = 0; i < _listenFds.Length; i++)
        {
            _listenFds[i] = OpenReusePortListener(_listenPorts[i], _config.ListenBacklog, _config.DualStack);
        }

        if (_incremental)
        {
            InitIncremental();
        }
        else
        {
            InitBufferRing();
        }

        _wakeFd = eventfd(0, EFD_NONBLOCK | EFD_CLOEXEC);
        if (_wakeFd < 0)
        {
            throw new InvalidOperationException("eventfd failed");
        }

        // Ring-native clients must be opened on this thread; async opens complete
        // once the loop starts.
        OnStart?.Invoke(this);

        Console.WriteLine($"[r{Id}] listening on 0.0.0.0:{string.Join(",", _listenPorts)} (incremental={_incremental})");
        foreach (int listenFd in _listenFds)
        {
            SubmitAcceptMultishot(listenFd);
        }
        ArmWakePoll();

        _timerTs = (__kernel_timespec*)NativeMemory.Alloc((nuint)sizeof(__kernel_timespec));
        _timerTs->tv_sec  = 0;
        _timerTs->tv_nsec = TimerIntervalNs;
        ArmTimer();

        if (_incremental)
        {
            LoopIncremental();
        }
        else
        {
            LoopShared();
        }

        foreach (int listenFd in _listenFds)
        {
            close(listenFd);
        }

        // Close any still-open accepted sockets (the connection table is indexed by fd) so they don't
        // leak when a host is disposed and recreated many times in one process - e.g. across a test run.
        for (int fd = 0; fd < _connections.Length; fd++)
        {
            if (_connections[fd] != null)
            {
                close(fd);
                _connections[fd] = null;
            }
        }

        close(_wakeFd);
        if (_timerTs != null)
        {
            NativeMemory.Free(_timerTs);
            _timerTs = null;
        }
        Ring.Dispose();

        // Shared provided-buffer ring (incremental mode allocates per connection instead). Freed after
        // the ring fd is closed, so the kernel has dropped its references to the slab.
        if (_bufRing != null)
        {
            NativeMemory.AlignedFree(_bufRing);
            _bufRing = null;
        }
        if (_bufSlab != null)
        {
            NativeMemory.AlignedFree(_bufSlab);
            _bufSlab = null;
        }
    }

    private void LoopShared()
    {
        while (!_stopRequested)
        {
            DrainReturnQ();
            DrainFlushQ();
            DrainRecycleQ();
            DrainRemoteOps();

            int rc = Ring.SubmitAndWait(1);
            if (rc < 0 && rc != -EINTR && rc != -EAGAIN && rc != -EBUSY)
            {
                Console.Error.WriteLine($"[r{Id}] io_uring_enter failed: {rc}");
                break;
            }

            uint ready = Ring.CqReady();
            for (uint i = 0; i < ready; i++)
            {
                Dispatch(in Ring.CqeAt(i));
            }
            Ring.CqAdvance(ready);
        }
    }

    private void Dispatch(in IoUringCqe cqe)
    {
        byte   kind = (byte)(cqe.user_data >> KindShift);
        ushort gen  = (ushort)(cqe.user_data >> GenShift);
        int    fd   = (int)(uint)cqe.user_data;
        bool   more = (cqe.flags & IORING_CQE_F_MORE) != 0;

        switch (kind)
        {
            case KindRecv:
            {
                bool   hasBuf = (cqe.flags & IORING_CQE_F_BUFFER) != 0;
                ushort bid    = hasBuf ? (ushort)(cqe.flags >> IORING_CQE_BUFFER_SHIFT) : (ushort)0;

                Connection? conn = ConnAt(fd, gen);

                if (cqe.res <= 0)
                {
                    // Peer EOF or recv error - reactor owns teardown.
                    if (hasBuf)
                    {
                        ReturnBufferDirect(bid);
                    }
                    if (conn != null)
                    {
                        _connections[fd] = null;
                        conn.MarkClosed();
                        conn.DecRef();
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

                byte* ptr = hasBuf ? _bufSlab + (nuint)bid * (nuint)RecvBufferSize : null;
                if (!conn.Complete(cqe.res, bid, hasBuf, ptr))
                {
                    // Recv queue overflow - tear down rather than zombify.
                    _connections[fd] = null;
                    SubmitCancel(Tag(KindRecv, gen, fd));
                    conn.MarkClosed();
                    conn.DecRef();
                    return;
                }

                if (!more)
                {
                    SubmitRecvMultishot(fd, gen, BgId);
                }
                return;
            }

            case KindSend:
                OnSendCompletion(fd, gen, cqe.res, cqe.flags);
                return;

            case KindClient:
                CompleteClient(fd, cqe.res);   // low 32 bits = op slot
                return;

            case KindAccept:
            {
                if (cqe.res >= 0)
                {
                    int clientFd = cqe.res;
                    SetNoDelay(clientFd);
                    Connection conn = _pool.TryPop(out var pooled)
                        ? pooled.SetFd(clientFd)
                        : new Connection(this, clientFd, _config.WriteSlabSize, _config.RecvQueueEntries, _config.WriteOverflow);
                    Track(clientFd, conn);
                    conn.InitRefs();
                    conn.UseZc = _zeroCopySend;   // config default; kTLS overrides to plain on handshake
                    conn.ListenerPort = PortOf(fd);
                    SubmitRecvMultishot(clientFd, (ushort)conn.Generation, BgId);

                    _ = RunHandlerAsync(conn);
                }
                else
                {
                    Console.Error.WriteLine($"[r{Id}] accept error: {cqe.res}");
                }
                if (!more)
                {
                    SubmitAcceptMultishot(fd);
                }
                return;
            }

            case KindWake:
                OnWakeCompletion(more);
                return;

            case KindTimer:
                OnTimerTick();
                return;

            case KindCancel:
                return;
        }
    }

    // Shared by both loops. Handles plain SEND (one CQE) and SEND_ZC (a data CQE carrying
    // IORING_CQE_F_MORE, then a separate IORING_CQE_F_NOTIF once the kernel releases the slab).
    private void OnSendCompletion(int fd, ushort gen, int res, uint cqeFlags)
    {
        Connection? conn = ConnAt(fd, gen);
        if (conn == null)
        {
            return;   // stale CQE - never touch the fd's new tenant
        }

        // Zero-copy buffer-release notification: the kernel is done with the slab. Recycle once the
        // data is fully sent and no further notifs are outstanding.
        if ((cqeFlags & IORING_CQE_F_NOTIF) != 0)
        {
            if (--conn.ZcNotifPending == 0 && conn.WriteHead >= conn.WriteInFlight)
            {
                conn.CompleteFlush();
            }
            return;
        }

        if (res <= 0)
        {
            _connections[fd] = null;
            SubmitCancel(Tag(KindRecv, gen, fd));   // the multishot recv is still armed
            conn.MarkClosed();
            conn.DecRef();
            return;
        }
        conn.WriteHead += res;

        // A zero-copy send posts its data CQE with F_MORE and a notif will follow; hold the slab until
        // that notif arrives. Plain SEND never sets F_MORE, so this is a no-op for it.
        if ((cqeFlags & IORING_CQE_F_MORE) != 0)
        {
            conn.ZcNotifPending++;
        }

        if (conn.WriteHead < conn.WriteInFlight)
        {
            // Partial send (rare with MSG_WAITALL): resubmit the remainder.
            if (conn.FlushVectored)
            {
                conn.AdvanceIov(res);   // trim the iovec past the bytes just sent
                SubmitSendMsg(conn, fd, gen);
            }
            else
            {
                SubmitSend(conn, fd, gen, conn.WriteBuffer + conn.WriteHead, (uint)(conn.WriteInFlight - conn.WriteHead), conn.SendOpFlags);
            }
            return;
        }

        // Data fully sent: plain SEND recycles now; a ZC send waits for its outstanding notif(s).
        if (conn.ZcNotifPending == 0)
        {
            conn.CompleteFlush();
        }
    }

    private void OnWakeCompletion(bool more)
    {
        // Drain the eventfd counter so the next write re-triggers POLLIN; queues
        // drain at the top of the next loop iteration.
        ulong drain;
        read(_wakeFd, &drain, 8);
        if (!more)
        {
            ArmWakePoll();
        }
    }

    /// <summary>
    /// Register a callback invoked on the reactor thread every timer interval (~250 ms). Call from
    /// <c>OnStart</c>. Pools use it to sweep per-command timeouts and replenish connections.
    /// </summary>
    public void AddTicker(Action ticker) => _tickers.Add(ticker);

    private void OnTimerTick()
    {
        for (int i = 0; i < _tickers.Count; i++)
        {
            try
            {
                _tickers[i]();
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[r{Id}] ticker faulted: {e.Message}");
            }
        }
        ArmTimer();   // single-shot timer; re-arm for the next interval
    }

    private void ArmTimer()
    {
        IoUringSqe* sqe = GetSqeOrFlush();
        Unsafe.InitBlockUnaligned(sqe, 0, 64);
        sqe->opcode    = IORING_OP_TIMEOUT;
        sqe->addr      = (ulong)_timerTs;
        sqe->len       = 1;
        sqe->off       = 0;            // pure time-based (no completion-count trigger)
        sqe->user_data = Tag(KindTimer, 0, 0);
    }

    // SQE producers - reactor-thread-only.
    private IoUringSqe* GetSqeOrFlush()
    {
        IoUringSqe* sqe = Ring.GetSqe();
        if (sqe != null)
        {
            return sqe;
        }

        // SQ is full: flush queued SQEs to the kernel (which frees ring slots) and retry. A few
        // submit rounds clear the transient fullness a large CQE batch can cause; only a genuinely
        // stuck ring falls through to throw, instead of crashing the reactor on the first miss.
        for (int attempt = 0; attempt < 16 && sqe == null; attempt++)
        {
            Ring.SubmitAndWait(0);
            sqe = Ring.GetSqe();
        }

        if (sqe == null)
        {
            throw new InvalidOperationException("io_uring SQ still full after repeated flush");
        }

        return sqe;
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

    // Submits the right send for a pending flush: a vectored SENDMSG for a segmented (multi-segment)
    // response, or the plain contiguous SEND for everything else (incl. the fast path and Grow mode).
    private void SubmitFlush(Connection conn, int fd, ushort gen)
    {
        if (conn.FlushVectored)
        {
            SubmitSendMsg(conn, fd, gen);
        }
        else
        {
            SubmitSend(conn, fd, gen, conn.WriteBuffer, (uint)conn.WriteInFlight, conn.SendOpFlags);
        }
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

    // Cancel by exact user_data so a dead connection's multishot recv can't keep
    // consuming buffers or race the fd's next tenant.
    private void SubmitCancel(ulong targetUserData)
    {
        IoUringSqe* sqe = GetSqeOrFlush();
        Unsafe.InitBlockUnaligned(sqe, 0, 64);
        sqe->opcode    = IORING_OP_ASYNC_CANCEL;
        sqe->fd        = -1;
        sqe->addr      = targetUserData;
        sqe->user_data = Tag(KindCancel, 0, 0);
    }

    private void Recycle(Connection conn, int fd)
    {
        conn.MarkClosed();
        SubmitCancel(Tag(KindRecv, (ushort)conn.Generation, fd));   // before Clear() bumps the generation

        if (_incremental)
        {
            TeardownConnectionBufRing(conn);   // per-conn ring freed wholesale
        }
        else
        {
            conn.DrainRecv();   // return leftover buffers to the shared ring
        }
        close(fd);
        conn.Clear();

        if (_pool.Count < PoolMax)
        {
            _pool.Push(conn);
        }
        else
        {
            conn.Dispose();
        }
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
