using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MinimaTFlow.Utils;
using static MinimaTFlow.Native;
// ReSharper disable SuggestVarOrType_BuiltInTypes

namespace MinimaTFlow;

/// <summary>
/// Recv-only io_uring reactor. Accept + multishot recv flow through the ring;
/// the response goes out via libc <c>send()</c> on the handler thread
/// (Twinflow-style), so the reactor never deals with sends. Cross-thread paths
/// (buffer-return, recycle) still go through the MPSC + eventfd-wake pattern.
/// </summary>
public sealed unsafe partial class Reactor
{
    public readonly int Id;
    public Ring Ring = null!;
    public readonly Dictionary<int,Connection> Connections = new();

    private int _listenFd;
    private readonly ServerConfig _config;
    private readonly ushort _port;
    private readonly uint _ringEntries;
    private readonly uint RecvBufferSize;

    private const ulong KindAccept = 1UL << 32;
    private const ulong KindRecv   = 2UL << 32;
    private const ulong KindWake   = 4UL << 32;

    private const ushort BgId = 1;
    private readonly uint BufferRingEntries;
    private byte*  _bufRing;
    private byte*  _bufSlab;
    private uint   _bufRingMask;
    private ushort _bufRingTail;

    private int _wakeFd;
    private int _reactorThreadId;
    private readonly Mpsc<ushort> _returnQ = new(1 << 14);
    private readonly ConcurrentQueue<Connection> _recycleQ = new();

    private readonly int PoolMax;
    private readonly Stack<Connection> _pool;

    private const int EINTR  = 4;
    private const int EAGAIN = 11;
    private const int EBUSY  = 16;

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

    internal void ReturnBufferDirect(ushort bid)
    {
        byte* slot = _bufRing + (_bufRingTail & _bufRingMask) * 16;
        *(ulong*)(slot + 0)  = (ulong)(_bufSlab + bid * (nuint)RecvBufferSize);
        *(uint*)(slot + 8)   = RecvBufferSize;
        *(ushort*)(slot + 12) = bid;
        _bufRingTail++;
        Volatile.Write(ref *(ushort*)(_bufRing + 14), _bufRingTail);
    }

    // =========================================================================
    // Cross-thread entry points
    // =========================================================================

    public void EnqueueReturnQ(ushort bid)
    {
        if (Environment.CurrentManagedThreadId == _reactorThreadId)
        {
            ReturnBufferDirect(bid);
            return;
        }
        SpinWait sw = default;
        while (!_returnQ.TryEnqueue(bid)) sw.SpinOnce();
        WakeFdWrite();
    }

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
        write(_wakeFd, &v, 8);
    }

    private void DrainReturnQ()
    {
        while (_returnQ.TryDequeue(out ushort bid))
        {
            ReturnBufferDirect(bid);
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
        sqe->op_flags  = POLLIN;
        sqe->len       = IORING_POLL_ADD_MULTI;
        sqe->user_data = KindWake | (uint)_wakeFd;
    }

    // =========================================================================
    // Main loop
    // =========================================================================

    public void Run()
    {
        _reactorThreadId = Environment.CurrentManagedThreadId;

        Ring = Ring.Create(_ringEntries);
        _listenFd = OpenReusePortListener(_port);

        InitBufferRing();

        _wakeFd = eventfd(0, EFD_NONBLOCK | EFD_CLOEXEC);
        if (_wakeFd < 0)
        {
            throw new InvalidOperationException("eventfd failed");
        }

        Console.WriteLine($"[r{Id}] listening on 0.0.0.0:{_port}");
        SubmitAcceptMultishot();
        ArmWakePoll();

        LoopShared();

        close(_listenFd);
        close(_wakeFd);
        Ring.Dispose();
    }

    private void LoopShared()
    {
        while (true)
        {
            DrainReturnQ();
            DrainRecycleQ();

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
        ulong kind = cqe.user_data & 0xffffffff_00000000UL;
        int   fd   = (int)(cqe.user_data & 0xffffffffUL);
        bool  more = (cqe.flags & IORING_CQE_F_MORE) != 0;

        if (kind == KindWake)
        {
            ulong drain;
            read(_wakeFd, &drain, 8);
            if (!more) ArmWakePoll();
            return;
        }

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
            if (!more) SubmitAcceptMultishot();
        }
        else if (kind == KindRecv)
        {
            bool   hasBuf = (cqe.flags & IORING_CQE_F_BUFFER) != 0;
            ushort bid    = hasBuf ? (ushort)(cqe.flags >> IORING_CQE_BUFFER_SHIFT) : (ushort)0;

            if (cqe.res <= 0)
            {
                if (hasBuf) ReturnBufferDirect(bid);
                if (Connections.Remove(fd, out var dyingConn))
                {
                    dyingConn.MarkClosed();
                    dyingConn.DecRef();
                }
                return;
            }

            if (!Connections.TryGetValue(fd, out var conn))
            {
                if (hasBuf) ReturnBufferDirect(bid);
                return;
            }

            byte* ptr = hasBuf ? _bufSlab + (nuint)bid * (nuint)RecvBufferSize : null;
            conn.Complete(cqe.res, bid, hasBuf, ptr);

            if (!more) SubmitRecvMultishot(fd);
        }
    }

    // =========================================================================
    // SQE producers — reactor-thread only (no send op; that's libc send() in
    // the handler).
    // =========================================================================

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
        if (fd < 0) throw new InvalidOperationException($"socket failed: {fd}");

        int one = 1;
        setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &one, sizeof(int));
        setsockopt(fd, SOL_SOCKET, SO_REUSEPORT, &one, sizeof(int));

        sockaddr_in addr = default;
        addr.sin_family      = AF_INET;
        addr.sin_port        = Htons(port);
        addr.sin_addr.s_addr = 0;

        if (bind(fd, &addr, (uint)sizeof(sockaddr_in)) < 0)
            throw new InvalidOperationException("bind failed");
        if (listen(fd, 128) < 0)
            throw new InvalidOperationException("listen failed");
        return fd;
    }
}
