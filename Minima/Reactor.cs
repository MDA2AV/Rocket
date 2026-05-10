using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static Minima.Native;
// ReSharper disable SuggestVarOrType_BuiltInTypes

namespace Minima;

/// <summary>
/// One reactor = one thread + one io_uring + one listening socket (SO_REUSEPORT)
/// + one connection map. Fully isolated from other reactors; the kernel
/// load-balances incoming connections across all SO_REUSEPORT listeners.
/// </summary>
internal sealed unsafe class Reactor
{
    public readonly int Id;
    public Ring Ring = null!;   // created on the reactor's own thread (DEFER_TASKRUN requires same-thread setup+enter)
    public readonly Dictionary<int, Connection> Connections = new();

    private int _listenFd;
    private readonly ushort _port;
    private readonly uint _ringEntries;

    // Provided-buffer ring (one per reactor, shared by all its connections).
    private const ushort BgId = 1;
    private const uint   BufferRingEntries = 4096;          // power of two
    private byte*  _bufRing;          // io_uring_buf_ring (kernel-shared)
    private byte*  _bufSlab;          // contiguous slab of recv buffers
    private uint   _bufRingMask;
    private ushort _bufRingTail;

    public Reactor(int id, ushort port, uint ringEntries)
    {
        Id = id;
        _port = port;
        _ringEntries = ringEntries;
    }

    // Buffer ring

    private void InitBufferRing()
    {
        nuint ringBytes = (nuint)BufferRingEntries * 16;
        _bufRing = (byte*)NativeMemory.AlignedAlloc(ringBytes, 4096);
        NativeMemory.Clear(_bufRing, ringBytes);

        nuint slabBytes = BufferRingEntries * (nuint)Program.BufferSize;
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

        // Populate every slot once. Slot 0 overlaps with the ring's tail field
        // at offset 14, but we only write addr/len/bid (offsets 0..13) so tail
        // stays at zero until we set it explicitly.
        for (ushort bid = 0; bid < BufferRingEntries; bid++) {
            byte* slot = _bufRing + (uint)bid * 16;
            *(ulong*)(slot + 0)  = (ulong)(_bufSlab + bid * (nuint)Program.BufferSize);
            *(uint*)(slot + 8)   = Program.BufferSize;
            *(ushort*)(slot + 12) = bid;
        }
        _bufRingTail = (ushort)BufferRingEntries;
        
        Volatile.Write(ref *(ushort*)(_bufRing + 14), _bufRingTail);
    }

    public void ReturnBuffer(ushort bid)
    {
        byte* slot = _bufRing + (_bufRingTail & _bufRingMask) * 16;
        *(ulong*)(slot + 0)  = (ulong)(_bufSlab + bid * (nuint)Program.BufferSize);
        *(uint*)(slot + 8)   = Program.BufferSize;
        *(ushort*)(slot + 12) = bid;
        _bufRingTail++;

        Volatile.Write(ref *(ushort*)(_bufRing + 14), _bufRingTail);
    }

    public void Run()
    {
        Ring = Ring.Create(_ringEntries);
        _listenFd = OpenReusePortListener(_port);
        
        InitBufferRing();

        Console.WriteLine($"[r{Id}] listening on 0.0.0.0:{_port}");
        SubmitAcceptMultishot();

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
        bool  more = (cqe.flags & IORING_CQE_F_MORE) != 0;

        if (kind == Program.KindAccept)
        {
            if (cqe.res >= 0)
            {
                int clientFd = cqe.res;
                var conn = new Connection(this);
                Connections[clientFd] = conn;
                SubmitRecvMultishot(clientFd);

                _ = Handler.HandleAsync(this, clientFd, conn);
            }
            else
            {
                Console.Error.WriteLine($"[r{Id}] accept error: {cqe.res}");
            }
            // Multishot accept stays armed; only re-arm if the kernel terminated it.
            if (!more)
            {
                SubmitAcceptMultishot();
            }
        }
        else if (kind == Program.KindRecv)
        {
            bool   hasBuf = (cqe.flags & IORING_CQE_F_BUFFER) != 0;
            ushort bid    = hasBuf ? (ushort)(cqe.flags >> IORING_CQE_BUFFER_SHIFT) : (ushort)0;

            if (!Connections.TryGetValue(fd, out var conn))
            {
                if (hasBuf) ReturnBuffer(bid);
                
                return;
            }

            conn.Complete(cqe.res, bid, hasBuf);

            if (!more && cqe.res > 0)
            {
                SubmitRecvMultishot(fd);
            }
        }
        else if (kind == Program.KindSend)
        {
            if (Connections.TryGetValue(fd, out var conn) && cqe.res <= 0)
            {
                conn.MarkClosed();
            }
        }
    }

    private IoUringSqe* GetSqeOrFlush()
    {
        IoUringSqe* sqe = Ring.GetSqe();
        if (sqe != null)
        {
            return sqe;
        }
        
        Ring.SubmitAndWait(0);
        sqe = Ring.GetSqe();
        
        if (sqe == null)
        {
            throw new InvalidOperationException("SQ full after flush");
        }
        
        return sqe;
    }

    private void SubmitAcceptMultishot()
    {
        IoUringSqe* sqe = GetSqeOrFlush();
        Unsafe.InitBlockUnaligned(sqe, 0, 64);
        sqe->opcode    = IORING_OP_ACCEPT;
        sqe->ioprio    = IORING_ACCEPT_MULTISHOT;
        sqe->fd        = _listenFd;
        sqe->user_data = Program.KindAccept | (uint)_listenFd;
    }

    public void SubmitRecvMultishot(int fd)
    {
        IoUringSqe* sqe = GetSqeOrFlush();
        Unsafe.InitBlockUnaligned(sqe, 0, 64);
        sqe->opcode    = IORING_OP_RECV;
        sqe->flags     = IOSQE_BUFFER_SELECT;
        sqe->ioprio    = IORING_RECV_MULTISHOT;
        sqe->fd        = fd;
        sqe->buf_index = BgId;          // same offset as buf_group in the kernel union
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
        addr.sin_addr.s_addr = 0; // 0.0.0.0

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
