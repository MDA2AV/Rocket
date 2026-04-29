using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static Minima.Native;

namespace Minima;

/// <summary>
/// Single-threaded TCP echo server using io_uring directly.
/// One-shot accept/recv/send re-armed per completion. No multishot,
/// no buffer rings, no async — just SQE in, CQE out.
/// </summary>
internal static unsafe class Program {
    private const ushort Port       = 8080;
    private const int    Backlog    = 128;
    private const int    BufferSize = 16 * 1024;

    // user_data layout: kind in high 32 bits, fd in low 32 bits.
    private const ulong KindAccept = 1UL << 32;
    private const ulong KindRecv   = 2UL << 32;
    private const ulong KindSend   = 3UL << 32;

    private sealed class Conn {
        public byte* Buffer;
    }

    private static readonly Dictionary<int, Conn> s_conns = new();

    private static int Main() {
        int listenFd = OpenListener(Port);
        Console.WriteLine($"[minima] listening on 0.0.0.0:{Port}");

        using var ring = Ring.Create(256);

        // Arm the first one-shot accept; subsequent accepts are re-armed in Dispatch.
        SubmitAccept(ring, listenFd);

        while (true) {
            int rc = ring.SubmitAndWait(1); // submit pending SQEs and block until 1+ CQE
            if (rc < 0 && rc != -4 /* EINTR */)
            {
                Console.Error.WriteLine($"[minima] io_uring_enter failed: {rc}");
                break;
            }

            while (ring.TryGetCqe(out IoUringCqe cqe))
            {
                Dispatch(ring, listenFd, in cqe);
                ring.CqeSeen();
            }
        }

        close(listenFd);
        return 0;
    }

    private static void Dispatch(Ring ring, int listenFd, in IoUringCqe cqe) {
        ulong kind = cqe.user_data & 0xffffffff_00000000UL;
        int   fd   = (int)(cqe.user_data & 0xffffffffUL);

        if (kind == KindAccept) {
            if (cqe.res >= 0) {
                int clientFd = cqe.res;
                Console.WriteLine($"[minima] accepted fd={clientFd}");

                var conn = new Conn { Buffer = (byte*)NativeMemory.Alloc((nuint)BufferSize) };
                s_conns[clientFd] = conn;
                SubmitRecv(ring, clientFd, conn.Buffer, BufferSize);
            } else {
                Console.Error.WriteLine($"[minima] accept error: {cqe.res}");
            }
            SubmitAccept(ring, listenFd);
        } else if (kind == KindRecv) {
            if (!s_conns.TryGetValue(fd, out var conn)) return;
            if (cqe.res <= 0) { CloseConn(fd, conn); return; }
            SubmitSend(ring, fd, conn.Buffer, (uint)cqe.res);
        } else if (kind == KindSend) {
            if (!s_conns.TryGetValue(fd, out var conn)) return;
            if (cqe.res <= 0) { CloseConn(fd, conn); return; }
            SubmitRecv(ring, fd, conn.Buffer, BufferSize);
        }
    }

    private static void CloseConn(int fd, Conn conn) {
        Console.WriteLine($"[minima] closing fd={fd}");
        NativeMemory.Free(conn.Buffer);
        s_conns.Remove(fd);
        close(fd);
    }
    
    // SQE prep
    private static void SubmitAccept(Ring ring, int listenFd) {
        IoUringSqe* sqe = ring.GetSqe();
        if (sqe == null) throw new InvalidOperationException("SQ full");

        Unsafe.InitBlockUnaligned(sqe, 0, 64);
        sqe->opcode    = IORING_OP_ACCEPT;
        sqe->fd        = listenFd;
        sqe->user_data = KindAccept | (uint)listenFd;
    }

    private static void SubmitRecv(Ring ring, int fd, byte* buf, int len) {
        IoUringSqe* sqe = ring.GetSqe();
        if (sqe == null) throw new InvalidOperationException("SQ full");

        Unsafe.InitBlockUnaligned(sqe, 0, 64);
        sqe->opcode    = IORING_OP_RECV;
        sqe->fd        = fd;
        sqe->addr      = (ulong)buf;
        sqe->len       = (uint)len;
        sqe->user_data = KindRecv | (uint)fd;
    }

    private static void SubmitSend(Ring ring, int fd, byte* buf, uint len) {
        IoUringSqe* sqe = ring.GetSqe();
        if (sqe == null) throw new InvalidOperationException("SQ full");

        Unsafe.InitBlockUnaligned(sqe, 0, 64);
        sqe->opcode    = IORING_OP_SEND;
        sqe->fd        = fd;
        sqe->addr      = (ulong)buf;
        sqe->len       = len;
        sqe->user_data = KindSend | (uint)fd;
    }
    
    // Listener socket
    private static int OpenListener(ushort port) {
        int fd = socket(AF_INET, SOCK_STREAM, 0);
        if (fd < 0) throw new InvalidOperationException($"socket failed: {fd}");

        int one = 1;
        setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &one, sizeof(int));

        sockaddr_in addr = default;
        addr.sin_family      = AF_INET;
        addr.sin_port        = Htons(port);
        addr.sin_addr.s_addr = 0; // 0.0.0.0

        if (bind(fd, &addr, (uint)sizeof(sockaddr_in)) < 0)
            throw new InvalidOperationException("bind failed");

        if (listen(fd, Backlog) < 0)
            throw new InvalidOperationException("listen failed");

        return fd;
    }
}
