using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MinimaSQPoll.Utils;
using static MinimaSQPoll.Native;
// ReSharper disable SuggestVarOrType_BuiltInTypes

namespace MinimaSQPoll;

/// <summary>
/// SQPOLL reactor: the kernel polls the SQ for us, so handler threads write
/// SQEs directly into ring memory via <see cref="Ring.TryGetSqe"/> +
/// <see cref="Ring.PublishSqe"/> (SpinLock-guarded). The reactor's only job is
/// to wait for CQEs and dispatch them — no MPSC queues, no eventfd wake, no
/// drain phase.
/// </summary>
public sealed unsafe partial class Reactor
{
    public readonly int Id;
    public Ring Ring = null!;   // created on the reactor's own thread
    public readonly ConcurrentDictionary<int,Connection> Connections = new();

    private int _listenFd;
    private readonly ServerConfig _config;
    private readonly ushort _port;
    private readonly uint _ringEntries;
    private readonly bool _incremental;
    private readonly uint RecvBufferSize;

    // CQE user_data layout: kind tag in the high 32 bits, fd in the low 32.
    private const ulong KindAccept = 1UL << 32;
    private const ulong KindRecv   = 2UL << 32;
    private const ulong KindSend   = 3UL << 32;

    // Provided-buffer ring (one per reactor, shared by all its connections).
    private const ushort BgId = 1;
    private readonly uint BufferRingEntries;                // power of two
    private byte*  _bufRing;          // io_uring_buf_ring (kernel-shared)
    private byte*  _bufSlab;          // contiguous slab of recv buffers
    private uint   _bufRingMask;
    private ushort _bufRingTail;

    // Guards multi-threaded updates to the buf_ring tail. Critical section is a
    // 16-byte write + a ushort tail bump, so SpinLock is the right primitive.
    private SpinLock _bufRingLock = new SpinLock(false);

    // Connection pool: accept runs on the reactor, recycle can run on any
    // thread (handler refcount → 0 off-reactor), so use the MPMC variant.
    private readonly int PoolMax;
    private readonly ConcurrentStack<Connection> _pool = new();

    // Incremental-mode (IOU_PBUF_RING_INC) sizing.
    private readonly int  MaxConnections;
    private readonly int  ConnBufRingEntries;
    private readonly uint IncRecvBufferSize;

    // Transient io_uring_enter errnos (Linux): interrupted, would-block, busy.
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
    }

    // =========================================================================
    // Buffer ring
    // =========================================================================

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

        for (ushort bid = 0; bid < BufferRingEntries; bid++) {
            byte* slot = _bufRing + (uint)bid * 16;
            *(ulong*)(slot + 0)  = (ulong)(_bufSlab + bid * (nuint)RecvBufferSize);
            *(uint*)(slot + 8)   = RecvBufferSize;
            *(ushort*)(slot + 12) = bid;
        }
        _bufRingTail = (ushort)BufferRingEntries;
        Volatile.Write(ref *(ushort*)(_bufRing + 14), _bufRingTail);
    }

    // Thread-safe buf_ring return — callable from any handler thread.
    internal void ReturnBufferDirect(ushort bid)
    {
        bool taken = false;
        _bufRingLock.Enter(ref taken);
        try
        {
            byte* slot = _bufRing + (_bufRingTail & _bufRingMask) * 16;
            *(ulong*)(slot + 0)  = (ulong)(_bufSlab + bid * (nuint)RecvBufferSize);
            *(uint*)(slot + 8)   = RecvBufferSize;
            *(ushort*)(slot + 12) = bid;
            _bufRingTail++;
            Volatile.Write(ref *(ushort*)(_bufRing + 14), _bufRingTail);
        }
        finally { _bufRingLock.Exit(); }
    }

    // =========================================================================
    // Cross-thread entry points — all run directly on the calling thread now,
    // no MPSC handoff, no eventfd wake. Synchronisation is via the SpinLocks
    // inside Ring (for SQ submit) and on _bufRingLock (for buf_ring return).
    // =========================================================================

    public void EnqueueReturnQ(ushort bid) => ReturnBufferDirect(bid);

    internal void EnqueueFlush(Connection conn)
    {
        SubmitSend(conn.ClientFd, conn.WriteBuffer, (uint)conn.WriteInFlight);
    }

    internal void EnqueueRecycle(Connection conn) => Recycle(conn, conn.ClientFd);

    // =========================================================================
    // Main loop
    // =========================================================================

    public void Run()
    {
        Ring = Ring.Create(_ringEntries);
        _listenFd = OpenReusePortListener(_port);

        InitBufferRing();

        Console.WriteLine($"[r{Id}] listening on 0.0.0.0:{_port}");
        SubmitAcceptMultishot();

        LoopShared();

        close(_listenFd);
        Ring.Dispose();
    }

    private void LoopShared()
    {
        while (true)
        {
            int rc = Ring.WaitForCqe(1);
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
                Connection conn = _pool.TryPop(out var pooled)
                    ? pooled.SetFd(clientFd)
                    : new Connection(this, clientFd, _config.WriteSlabSize);
                Connections[clientFd] = conn;
                conn.InitRefs();
                SubmitRecvMultishot(clientFd);

                _ = _config.UsePipe
                    ? Handler.HandlePipeAsync(this, conn)
                    : Handler.HandleAsync(this, conn);
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
                if (hasBuf)
                {
                    ReturnBufferDirect(bid);
                }
                if (Connections.TryRemove(fd, out var dyingConn))
                {
                    dyingConn.MarkClosed();
                    dyingConn.DecRef();
                }
                return;
            }

            if (!Connections.TryGetValue(fd, out var conn))
            {
                if (hasBuf)
                {
                    ReturnBufferDirect(bid);
                }
                return;
            }

            byte* ptr = hasBuf ? _bufSlab + (nuint)bid * (nuint)RecvBufferSize : null;
            conn.Complete(cqe.res, bid, hasBuf, ptr);

            if (!more)
            {
                SubmitRecvMultishot(fd);
            }
        }
        else if (kind == KindSend)
        {
            if (!Connections.TryGetValue(fd, out var conn))
            {
                return;
            }
            if (cqe.res <= 0)
            {
                if (Connections.TryRemove(fd, out _))
                {
                    conn.MarkClosed();
                    conn.DecRef();
                }
                return;
            }
            conn.WriteHead += cqe.res;
            if (conn.WriteHead < conn.WriteInFlight)
            {
                SubmitSend(fd, conn.WriteBuffer + conn.WriteHead, (uint)(conn.WriteInFlight - conn.WriteHead));
                return;
            }
            conn.CompleteFlush();
        }
    }

    // =========================================================================
    // SQE producers — thread-safe via Ring's submit SpinLock. Each call is
    // one allocate-write-publish cycle, so any thread can submit directly.
    // =========================================================================

    private IoUringSqe* GetSqe()
    {
        IoUringSqe* sqe = Ring.TryGetSqe();
        if (sqe == null)
        {
            throw new InvalidOperationException("SQ full");
        }
        return sqe;
    }

    private void SubmitAcceptMultishot()
    {
        IoUringSqe* sqe = GetSqe();
        Unsafe.InitBlockUnaligned(sqe, 0, 64);
        sqe->opcode    = IORING_OP_ACCEPT;
        sqe->ioprio    = IORING_ACCEPT_MULTISHOT;
        sqe->fd        = _listenFd;
        sqe->user_data = KindAccept | (uint)_listenFd;
        Ring.PublishSqe();
    }

    private void SubmitRecvMultishot(int fd) => SubmitRecvMultishot(fd, BgId);

    private void SubmitRecvMultishot(int fd, ushort bgid)
    {
        IoUringSqe* sqe = GetSqe();
        Unsafe.InitBlockUnaligned(sqe, 0, 64);
        sqe->opcode    = IORING_OP_RECV;
        sqe->flags     = IOSQE_BUFFER_SELECT;
        sqe->ioprio    = IORING_RECV_MULTISHOT;
        sqe->fd        = fd;
        sqe->buf_index = bgid;
        sqe->user_data = KindRecv | (uint)fd;
        Ring.PublishSqe();
    }

    private void SubmitSend(int fd, byte* buf, uint len)
    {
        IoUringSqe* sqe = GetSqe();
        Unsafe.InitBlockUnaligned(sqe, 0, 64);
        sqe->opcode    = IORING_OP_SEND;
        sqe->fd        = fd;
        sqe->addr      = (ulong)buf;
        sqe->len       = len;
        sqe->user_data = KindSend | (uint)fd;
        Ring.PublishSqe();
    }

    private void Recycle(Connection conn, int fd)
    {
        conn.MarkClosed();
        conn.DrainRecv();
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

    private static void SetNoDelay(int fd)
    {
        int one = 1;
        setsockopt(fd, IPPROTO_TCP, TCP_NODELAY, &one, sizeof(int));
    }

    private static int OpenReusePortListener(ushort port)
    {
        int fd = socket(AF_INET, SOCK_STREAM, 0);
        if (fd < 0)
        {
            throw new InvalidOperationException($"socket failed: {fd}");
        }

        int one = 1;
        setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &one, sizeof(int));
        setsockopt(fd, SOL_SOCKET, SO_REUSEPORT, &one, sizeof(int));

        sockaddr_in addr = default;
        addr.sin_family      = AF_INET;
        addr.sin_port        = Htons(port);
        addr.sin_addr.s_addr = 0;

        if (bind(fd, &addr, (uint)sizeof(sockaddr_in)) < 0)
        {
            throw new InvalidOperationException("bind failed");
        }

        if (listen(fd, 128) < 0)
        {
            throw new InvalidOperationException("listen failed");
        }

        return fd;
    }
}
