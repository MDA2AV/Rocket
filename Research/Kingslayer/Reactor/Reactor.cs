using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static Kingslayer.Native;
// ReSharper disable SuggestVarOrType_BuiltInTypes

namespace Kingslayer;

/// <summary>
/// EXPERIMENT variant: no SynchronizationContext, no queues. The ring is created WITHOUT
/// SINGLE_ISSUER / DEFER_TASKRUN, the IVTS continuations resume on the .NET thread pool
/// (RunContinuationsAsynchronously = true), and handlers submit their own sends straight from their
/// pool thread. Because that lifts only the *kernel* single-submitter restriction — the userspace SQ,
/// buf_ring, and connection pool are still not thread-safe — a single <see cref="_lock"/> serializes
/// all submission + buffer-return + recycle. The reactor thread only accepts and reaps completions
/// (its wait is lockless); everything that mutates shared ring state takes the lock.
/// </summary>
public sealed unsafe class Reactor
{
    public readonly int Id;
    public Ring Ring = null!;
    public readonly ConcurrentDictionary<int, Connection> Connections = new();

    private int _listenFd;
    private readonly ServerConfig _config;
    private readonly ushort _port;
    private readonly uint _ringEntries;
    private readonly uint RecvBufferSize;

    private const ulong KindAccept = 1UL << 32;
    private const ulong KindRecv   = 2UL << 32;
    private const ulong KindSend   = 3UL << 32;

    private const ushort BgId = 1;
    private readonly uint BufferRingEntries;
    private byte*  _bufRing;
    private byte*  _bufSlab;
    private uint   _bufRingMask;
    private ushort _bufRingTail;

    private readonly int PoolMax;
    private readonly Stack<Connection> _pool;

    private const int EINTR  = 4;
    private const int EAGAIN = 11;
    private const int EBUSY  = 16;

    private int _reactorThreadId;
    public int ThreadId => _reactorThreadId;

    // Serializes every userspace mutation of shared ring state: SQ submission, the buf_ring tail, and
    // the connection pool. Taken by the reactor's dispatch AND by handler pool threads. The reactor's
    // blocking wait (Ring.Wait) and CQ reaping are NOT under it (CQ is reactor-only).
    private readonly object _lock = new();

    public Reactor(int id, ServerConfig config)
    {
        Id = id;
        _config = config;
        _port = config.Port;
        _ringEntries = config.RingEntries;
        RecvBufferSize = (uint)config.RecvBufferSize;
        BufferRingEntries = (uint)config.BufferRingEntries;
        PoolMax = config.PoolMax;
        _pool = new Stack<Connection>(config.PoolMax);
    }

    private void InitBufferRing()
    {
        nuint ringBytes = (nuint)BufferRingEntries * 16;
        _bufRing = (byte*)NativeMemory.AlignedAlloc(ringBytes, 4096);
        NativeMemory.Clear(_bufRing, ringBytes);

        nuint slabBytes = BufferRingEntries * (nuint)RecvBufferSize;
        _bufSlab = (byte*)NativeMemory.AlignedAlloc(slabBytes, 64);

        _bufRingMask = BufferRingEntries - 1;

        var reg = new io_uring_buf_reg
        {
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

        for (ushort bid = 0; bid < BufferRingEntries; bid++)
        {
            byte* slot = _bufRing + (uint)bid * 16;
            *(ulong*)(slot + 0)   = (ulong)(_bufSlab + bid * (nuint)RecvBufferSize);
            *(uint*)(slot + 8)    = RecvBufferSize;
            *(ushort*)(slot + 12) = bid;
        }
        _bufRingTail = (ushort)BufferRingEntries;

        Volatile.Write(ref *(ushort*)(_bufRing + 14), _bufRingTail);
    }

    // Re-add a consumed recv buffer. Called from the reactor AND handler pool threads → locked.
    internal void ReturnBufferDirect(ushort bid)
    {
        lock (_lock)
        {
            byte* slot = _bufRing + (_bufRingTail & _bufRingMask) * 16;
            *(ulong*)(slot + 0)   = (ulong)(_bufSlab + bid * (nuint)RecvBufferSize);
            *(uint*)(slot + 8)    = RecvBufferSize;
            *(ushort*)(slot + 12) = bid;
            _bufRingTail++;
            Volatile.Write(ref *(ushort*)(_bufRing + 14), _bufRingTail);
        }
    }

    public void Run()
    {
        _reactorThreadId = Environment.CurrentManagedThreadId;

        Ring = Ring.Create(_ringEntries);
        _listenFd = OpenReusePortListener(_port);
        InitBufferRing();

        Console.WriteLine($"[r{Id}] listening on 0.0.0.0:{_port}");
        SubmitAcceptMultishot();
        Loop();

        close(_listenFd);
        Ring.Dispose();
    }

    private void Loop()
    {
        while (true)
        {
            // Lockless wait: producers (handlers + dispatch) submit under _lock; we only reap here.
            int rc = Ring.Wait();
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
        ulong kind = cqe.user_data & 0xffffffff_00000000UL;
        int   fd   = (int)(cqe.user_data & 0xffffffffUL);
        bool  more = (cqe.flags & IORING_CQE_F_MORE) != 0;

        if (kind == KindAccept)
        {
            if (cqe.res >= 0)
            {
                int clientFd = cqe.res;
                SetNoDelay(clientFd);
                Connection conn;
                lock (_lock)
                {
                    conn = _pool.TryPop(out var pooled)
                        ? pooled.SetFd(clientFd)
                        : new Connection(this, clientFd, _config.WriteSlabSize);
                }
                Connections[clientFd] = conn;
                conn.InitRefs();
                SubmitRecvMultishot(clientFd);
                // First await suspends; the handler then runs on the thread pool from here on.
                _ = _config.Handler(this, conn);
            }
            else
            {
                Console.Error.WriteLine($"[r{Id}] accept error: {cqe.res}");
            }
            if (!more)
            {
                SubmitAcceptMultishot();
            }
        }
        else if (kind == KindRecv)
        {
            bool   hasBuf = (cqe.flags & IORING_CQE_F_BUFFER) != 0;
            ushort bid    = hasBuf ? (ushort)(cqe.flags >> IORING_CQE_BUFFER_SHIFT) : (ushort)0;

            if (cqe.res <= 0)
            {
                if (hasBuf) ReturnBufferDirect(bid);
                if (Connections.TryRemove(fd, out var dying))
                {
                    dying.MarkClosed();
                    dying.DecRef();
                }
                return;
            }

            if (!Connections.TryGetValue(fd, out var conn))
            {
                if (hasBuf) ReturnBufferDirect(bid);
                return;
            }

            byte* ptr = hasBuf ? _bufSlab + (nuint)bid * (nuint)RecvBufferSize : null;
            conn.Complete(cqe.res, bid, hasBuf, ptr);   // SetResult → handler resumes on the pool

            if (!more) SubmitRecvMultishot(fd);
        }
        else if (kind == KindSend)
        {
            if (!Connections.TryGetValue(fd, out var conn)) return;
            if (cqe.res <= 0)
            {
                Connections.TryRemove(fd, out _);
                conn.MarkClosed();
                conn.DecRef();
                return;
            }
            conn.WriteHead += cqe.res;
            if (conn.WriteHead < conn.WriteInFlight)
            {
                SubmitSend(fd, conn.WriteBuffer + conn.WriteHead, (uint)(conn.WriteInFlight - conn.WriteHead));
                return;
            }
            conn.CompleteFlush();   // SetResult → handler resumes on the pool
        }
    }

    // GetSqe must be called under _lock (mutates _sqeTail / the SQ array).
    private IoUringSqe* GetSqeOrFlush()
    {
        IoUringSqe* sqe = Ring.GetSqe();
        if (sqe != null) return sqe;

        Ring.SubmitAndWait(0);
        sqe = Ring.GetSqe();
        if (sqe == null) throw new InvalidOperationException("SQ full after flush");
        return sqe;
    }

    private void SubmitAcceptMultishot()
    {
        lock (_lock)
        {
            IoUringSqe* sqe = GetSqeOrFlush();
            Unsafe.InitBlockUnaligned(sqe, 0, 64);
            sqe->opcode    = IORING_OP_ACCEPT;
            sqe->ioprio    = IORING_ACCEPT_MULTISHOT;
            sqe->fd        = _listenFd;
            sqe->user_data = KindAccept | (uint)_listenFd;
            Ring.SubmitAndWait(0);
        }
    }

    private void SubmitRecvMultishot(int fd) => SubmitRecvMultishot(fd, BgId);

    private void SubmitRecvMultishot(int fd, ushort bgid)
    {
        lock (_lock)
        {
            IoUringSqe* sqe = GetSqeOrFlush();
            Unsafe.InitBlockUnaligned(sqe, 0, 64);
            sqe->opcode    = IORING_OP_RECV;
            sqe->flags     = IOSQE_BUFFER_SELECT;
            sqe->ioprio    = IORING_RECV_MULTISHOT;
            sqe->fd        = fd;
            sqe->buf_index = bgid;
            sqe->user_data = KindRecv | (uint)fd;
            Ring.SubmitAndWait(0);
        }
    }

    // Called by the reactor (partial-send resubmit) AND by Connection.FlushAsync from a pool thread.
    internal void SubmitSend(int fd, byte* buf, uint len)
    {
        lock (_lock)
        {
            IoUringSqe* sqe = GetSqeOrFlush();
            Unsafe.InitBlockUnaligned(sqe, 0, 64);
            sqe->opcode    = IORING_OP_SEND;
            sqe->fd        = fd;
            sqe->addr      = (ulong)buf;
            sqe->len       = len;
            sqe->user_data = KindSend | (uint)fd;
            Ring.SubmitAndWait(0);
        }
    }

    internal void Recycle(Connection conn, int fd)
    {
        conn.MarkClosed();
        conn.DrainRecv();   // returns leftover buffers via ReturnBufferDirect (locks)
        close(fd);
        conn.Clear();

        lock (_lock)
        {
            if (_pool.Count < PoolMax) _pool.Push(conn);
            else conn.Dispose();
        }
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
        addr.sin_addr.s_addr = 0;

        if (bind(fd, &addr, (uint)sizeof(sockaddr_in)) < 0) throw new InvalidOperationException("bind failed");
        if (listen(fd, 128) < 0) throw new InvalidOperationException("listen failed");
        return fd;
    }
}
