using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using static Raptor.Native;

namespace Raptor;

/// <summary>
/// One reactor = one thread + one io_uring + one SO_REUSEPORT listener + a shard
/// of connections. The reactor thread drains completions (accept/recv/send-done)
/// and arms recv/accept. SENDS, however, are submitted by the per-connection
/// output pump running on the thread pool — they only take the SQ lock to write
/// the SQE, then io_uring_enter themselves. No eventfd handoff to the reactor.
/// </summary>
public sealed unsafe class RaptorReactor
{
    public readonly int Id;
    private readonly ushort _port;
    private readonly uint _ringEntries;
    private readonly int _recvBufSize;
    private readonly int _backlog;

    private RaptorRing _ring = null!;
    private int _listenFd;
    private readonly object _sqLock = new();
    private readonly ConcurrentDictionary<int, RaptorConnection> _conns = new();
    private long _nextId;
    private volatile bool _running = true;

    private const ulong KindAccept = 1UL << 32;
    private const ulong KindRecv   = 2UL << 32;
    private const ulong KindSend   = 3UL << 32;

    private const int EINTR = 4, EAGAIN = 11, EBUSY = 16;

    internal Action<RaptorConnection>? OnAccept;

    public RaptorReactor(int id, ushort port, uint ringEntries, int recvBufSize, int backlog)
    {
        Id = id;
        _port = port;
        _ringEntries = ringEntries;
        _recvBufSize = recvBufSize;
        _backlog = backlog;
    }

    public void Run()
    {
        _ring = RaptorRing.Create(_ringEntries);
        _listenFd = OpenReusePortListener(_port, _backlog);
        Console.WriteLine($"[raptor r{Id}] listening on 0.0.0.0:{_port}");
        ArmAccept();

        while (_running)
        {
            int rc = _ring.SubmitAndWait();   // submit queued (accept/recv re-arms) + wait
            if (rc < 0 && rc != -EINTR && rc != -EAGAIN && rc != -EBUSY)
            {
                Console.Error.WriteLine($"[raptor r{Id}] io_uring_enter failed: {rc}");
                break;
            }

            uint ready = _ring.CqReady();
            for (uint i = 0; i < ready; i++)
                Dispatch(in _ring.CqeAt(i));
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
            if (cqe.res >= 0) AcceptOne(cqe.res);
            else Console.Error.WriteLine($"[raptor r{Id}] accept error {cqe.res}");
            if (!more) ArmAccept();   // multishot terminated — re-arm
            return;
        }

        if (kind == KindRecv)
        {
            if (!_conns.TryGetValue(fd, out var conn)) return;
            if (cqe.res <= 0) { conn.OnRecvClosed(); return; }  // peer FIN / error
            conn.OnRecv(cqe.res);
            ArmRecv(conn);            // single-shot recv — re-arm
            return;
        }

        if (kind == KindSend)
        {
            if (_conns.TryGetValue(fd, out var conn))
                conn.CompleteSend(cqe.res);
        }
    }

    private void AcceptOne(int clientFd)
    {
        SetNoDelay(clientFd);
        long id = ((long)Id << 48) | (Interlocked.Increment(ref _nextId) & 0xffff_ffff_ffff);
        var conn = new RaptorConnection(this, clientFd, id, _recvBufSize);
        _conns[clientFd] = conn;
        ArmRecv(conn);
        OnAccept?.Invoke(conn);
        _ = conn.RunOutputPumpAsync();
    }

    // ---- submission (SQ writes serialised by _sqLock) ----

    private void ArmAccept()
    {
        lock (_sqLock)
        {
            IoUringSqe* sqe = _ring.GetSqe();
            if (sqe == null) return;
            Unsafe.InitBlockUnaligned(sqe, 0, 64);
            sqe->opcode    = IORING_OP_ACCEPT;
            sqe->fd        = _listenFd;
            sqe->ioprio    = IORING_ACCEPT_MULTISHOT;
            sqe->user_data = KindAccept;
            _ring.PublishTail();
        }
        // submitted by the next SubmitAndWait
    }

    private void ArmRecv(RaptorConnection conn)
    {
        lock (_sqLock)
        {
            IoUringSqe* sqe = _ring.GetSqe();
            if (sqe == null) return;
            Unsafe.InitBlockUnaligned(sqe, 0, 64);
            sqe->opcode    = IORING_OP_RECV;
            sqe->fd        = conn.Fd;
            sqe->addr      = (ulong)conn.RecvBuf;
            sqe->len       = (uint)conn.RecvBufSize;
            sqe->user_data = KindRecv | (uint)conn.Fd;
            _ring.PublishTail();
        }
    }

    /// <summary>
    /// Off-reactor send: called from the connection's output pump (a thread-pool
    /// thread). Pins the segment, writes the send SQE under the SQ lock, then
    /// io_uring_enter()s itself — no handoff to the reactor. The reactor completes
    /// the returned task when the send CQE arrives.
    /// </summary>
    internal Task<int> SendAsync(RaptorConnection conn, ReadOnlyMemory<byte> mem)
    {
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        System.Buffers.MemoryHandle pin = mem.Pin();

        lock (_sqLock)
        {
            IoUringSqe* sqe = _ring.GetSqe();
            if (sqe == null)
            {
                pin.Dispose();
                tcs.TrySetResult(-1);
                return tcs.Task;
            }
            conn.SetPendingSend(tcs, pin);
            Unsafe.InitBlockUnaligned(sqe, 0, 64);
            sqe->opcode    = IORING_OP_SEND;
            sqe->fd        = conn.Fd;
            sqe->addr      = (ulong)pin.Pointer;
            sqe->len       = (uint)mem.Length;
            sqe->op_flags  = MSG_NOSIGNAL;
            sqe->user_data = KindSend | (uint)conn.Fd;
            _ring.PublishTail();
        }
        _ring.Flush();   // submit our own send — no reactor wake needed
        return tcs.Task;
    }

    internal void Remove(RaptorConnection conn) =>
        _conns.TryRemove(new KeyValuePair<int, RaptorConnection>(conn.Fd, conn));

    public void Stop() => _running = false;

    // ---- listener ----
    private static int OpenReusePortListener(ushort port, int backlog)
    {
        int fd = socket(AF_INET, SOCK_STREAM, 0);
        if (fd < 0) throw new InvalidOperationException("socket() failed");

        int one = 1;
        setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &one, sizeof(int));
        setsockopt(fd, SOL_SOCKET, SO_REUSEPORT, &one, sizeof(int));

        sockaddr_in addr = default;
        addr.sin_family      = AF_INET;
        addr.sin_port        = Htons(port);
        addr.sin_addr.s_addr = 0;   // INADDR_ANY
        if (bind(fd, &addr, (uint)sizeof(sockaddr_in)) < 0)
            throw new InvalidOperationException($"bind(:{port}) failed");
        if (listen(fd, backlog) < 0)
            throw new InvalidOperationException("listen() failed");
        return fd;
    }

    private static void SetNoDelay(int fd)
    {
        int one = 1;
        setsockopt(fd, IPPROTO_TCP, TCP_NODELAY, &one, sizeof(int));
    }
}
