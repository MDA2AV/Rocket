using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static Minima.Native;

namespace Minima;

/// <summary>
/// One reactor = one thread + one io_uring + one listening socket (SO_REUSEPORT)
/// + one connection map. Fully isolated from other reactors; the kernel
/// load-balances incoming connections across all SO_REUSEPORT listeners.
/// </summary>
internal sealed unsafe class Reactor
{
    public readonly int Id;
    public readonly Ring Ring;
    public readonly Dictionary<int, Connection> Connections = new();

    private readonly int _listenFd;
    private readonly ushort _port;

    public Reactor(int id, ushort port, uint ringEntries)
    {
        Id = id;
        _port = port;
        Ring = Ring.Create(ringEntries);
        _listenFd = OpenReusePortListener(port);
    }

    public void Run()
    {
        Console.WriteLine($"[r{Id}] listening on 0.0.0.0:{_port}");
        SubmitAccept();

        while (true)
        {
            int rc = Ring.SubmitAndWait(1);
            if (rc < 0 && rc != -4 /* EINTR */)
            {
                Console.Error.WriteLine($"[r{Id}] io_uring_enter failed: {rc}");
                break;
            }

            while (Ring.TryGetCqe(out IoUringCqe cqe))
            {
                Dispatch(in cqe);
                Ring.CqeSeen();
            }
        }

        close(_listenFd);
        Ring.Dispose();
    }

    private void Dispatch(in IoUringCqe cqe)
    {
        ulong kind = cqe.user_data & 0xffffffff_00000000UL;
        int   fd   = (int)(cqe.user_data & 0xffffffffUL);

        if (kind == Program.KindAccept)
        {
            if (cqe.res >= 0)
            {
                int clientFd = cqe.res;
                var conn = new Connection(this) { Buffer = (byte*)NativeMemory.Alloc((nuint)Program.BufferSize) };
                Connections[clientFd] = conn;
                _ = Handler.HandleAsync(this, clientFd, conn);
            }
            else
            {
                Console.Error.WriteLine($"[r{Id}] accept error: {cqe.res}");
            }
            SubmitAccept();
        }
        else if (kind == Program.KindRecv)
        {
            if (Connections.TryGetValue(fd, out var conn))
                conn.Complete(cqe.res);
        }
        else if (kind == Program.KindSend)
        {
            if (Connections.TryGetValue(fd, out var conn) && cqe.res <= 0)
                conn.MarkClosed();
        }
    }

    // ---- SQE prep (per-reactor; no static ring sharing) ----

    private IoUringSqe* GetSqeOrFlush()
    {
        IoUringSqe* sqe = Ring.GetSqe();
        if (sqe != null) return sqe;
        Ring.SubmitAndWait(0);
        sqe = Ring.GetSqe();
        if (sqe == null) throw new InvalidOperationException("SQ full after flush");
        return sqe;
    }

    private void SubmitAccept()
    {
        IoUringSqe* sqe = GetSqeOrFlush();
        Unsafe.InitBlockUnaligned(sqe, 0, 64);
        sqe->opcode    = IORING_OP_ACCEPT;
        sqe->fd        = _listenFd;
        sqe->user_data = Program.KindAccept | (uint)_listenFd;
    }

    public void SubmitRecv(int fd, byte* buf, int len)
    {
        IoUringSqe* sqe = GetSqeOrFlush();
        Unsafe.InitBlockUnaligned(sqe, 0, 64);
        sqe->opcode    = IORING_OP_RECV;
        sqe->fd        = fd;
        sqe->addr      = (ulong)buf;
        sqe->len       = (uint)len;
        sqe->user_data = Program.KindRecv | (uint)fd;
    }

    public void SubmitSend(int fd, byte* buf, uint len)
    {
        IoUringSqe* sqe = GetSqeOrFlush();
        Unsafe.InitBlockUnaligned(sqe, 0, 64);
        sqe->opcode    = IORING_OP_SEND;
        sqe->fd        = fd;
        sqe->addr      = (ulong)buf;
        sqe->len       = len;
        sqe->user_data = Program.KindSend | (uint)fd;
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

        if (bind(fd, &addr, (uint)sizeof(sockaddr_in)) < 0)
            throw new InvalidOperationException("bind failed");

        if (listen(fd, 128) < 0)
            throw new InvalidOperationException("listen failed");

        return fd;
    }
}
