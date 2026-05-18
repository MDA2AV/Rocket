using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static MinimaZero.Native;
using static MinimaZero.Program;
// ReSharper disable SuggestVarOrType_BuiltInTypes

namespace MinimaZero;

/// <summary>
/// One reactor = one thread + one io_uring + one listening socket + one
/// connection map + one registered zcrx interface queue.
/// <para>
/// Unlike provided-buffer Minima, zcrx binds this ring to exactly ONE NIC
/// hardware Rx queue (<see cref="_ifIdx"/>, <see cref="_ifRxq"/>). Only TCP
/// flows the NIC steers to that queue are zero-copied. Multi-reactor +
/// SO_REUSEPORT load-balancing does NOT compose with that (each ring would
/// need its own steered HW queue), so this teaching variant runs a single
/// reactor. See Program.cs for the required ethtool host setup.
/// </para>
/// </summary>
internal sealed unsafe class Reactor
{
    public readonly int Id;
    public Ring Ring = null!;   // created on the reactor's own thread (DEFER_TASKRUN requires same-thread setup+enter)
    public readonly Dictionary<int, Connection> Connections = new();

    private int _listenFd;
    private readonly ushort _port;
    private readonly uint _ringEntries;

    // NIC interface index + hardware Rx queue this ring's zcrx ifq binds to.
    private readonly uint _ifIdx;
    private readonly uint _ifRxq;

    // zcrx geometry. The area is carved into RqEntries fixed PageChunk-sized
    // slots; one refill-queue entry recycles one slot. Keeping AreaLen ==
    // RqEntries * PageChunk means one rqe per chunk and a trivial initial fill.
    private const uint  RqEntries = 4096;          // power of two
    private const nuint PageChunk = 4096;          // rx_buf_len (default = PAGE_SIZE)
    private const nuint AreaLen   = RqEntries * PageChunk;   // 16 MiB

    private byte* _area;            // NIC DMAs payload here (registered)
    private nuint _areaLen;
    private byte* _rqRegion;        // refill ring backing memory (user region)
    private nuint _rqRegionLen;

    private uint*          _rqHead;   // kernel-owned consumer index (we read)
    private uint*          _rqTail;   // userspace-owned producer index (we bump)
    private IoUringZcrxRqe* _rqes;    // refill entry array
    private uint           _rqMask;
    private uint           _rqTailLocal;

    public Reactor(int id, ushort port, uint ringEntries, uint ifIdx, uint ifRxq)
    {
        Id = id;
        _port = port;
        _ringEntries = ringEntries;
        _ifIdx = ifIdx;
        _ifRxq = ifRxq;
    }

    // zcrx setup

    private void InitZcrx()
    {
        _areaLen = AreaLen;
        _area = (byte*)MmapAnon(_areaLen);
        if (_area == (byte*)-1)
            throw new InvalidOperationException("mmap(zcrx area) failed");

        // Region must hold the rqe array plus the kernel's head/tail control
        // words. One extra page covers the control area with room to spare.
        nuint rqBytes = RqEntries * (nuint)sizeof(IoUringZcrxRqe);
        _rqRegionLen = AlignUp(rqBytes + 4096, 4096);
        _rqRegion = (byte*)MmapAnon(_rqRegionLen);
        if (_rqRegion == (byte*)-1)
            throw new InvalidOperationException("mmap(zcrx refill region) failed");

        IoUringZcrxAreaReg areaReg = default;
        areaReg.addr = (ulong)_area;
        areaReg.len  = _areaLen;

        IoUringRegionDesc regionDesc = default;
        regionDesc.user_addr = (ulong)_rqRegion;
        regionDesc.size      = _rqRegionLen;
        regionDesc.flags     = IORING_MEM_REGION_TYPE_USER;

        IoUringZcrxIfqReg ifqReg = default;
        ifqReg.if_idx     = _ifIdx;
        ifqReg.if_rxq     = _ifRxq;
        ifqReg.rq_entries = RqEntries;
        ifqReg.area_ptr   = (ulong)&areaReg;
        ifqReg.region_ptr = (ulong)&regionDesc;

        int ret = io_uring_register(Ring.Fd, IORING_REGISTER_ZCRX_IFQ, &ifqReg, 1);
        if (ret < 0)
        {
            int err = Marshal.GetLastPInvokeError();
            throw new InvalidOperationException(
                $"register zcrx ifq failed: ret={ret} errno={err}. " +
                $"Check kernel >= 6.15, a zcrx-capable driver, and that " +
                $"if_idx={_ifIdx}/if_rxq={_ifRxq} exists with tcp-data-split on.");
        }

        _rqHead = (uint*)(_rqRegion + ifqReg.offsets.head);
        _rqTail = (uint*)(_rqRegion + ifqReg.offsets.tail);
        _rqes   = (IoUringZcrxRqe*)(_rqRegion + ifqReg.offsets.rqes);
        _rqMask = RqEntries - 1;

        // Prime the refill ring with every chunk so the NIC has somewhere to
        // DMA into. Area id 0 => the token is just the byte offset.
        for (uint i = 0; i < RqEntries; i++)
        {
            _rqes[i].off = (ulong)i * PageChunk;
            _rqes[i].len = (uint)PageChunk;
        }
        _rqTailLocal = RqEntries;
        Volatile.Write(ref *_rqTail, _rqTailLocal);
    }

    /// <summary>
    /// Hand a consumed chunk back to the kernel. <paramref name="off"/> is the
    /// rcqe.off token verbatim (area id in the high bits is preserved).
    /// </summary>
    public void RefillRqe(ulong off, uint len)
    {
        uint slot = _rqTailLocal & _rqMask;
        _rqes[slot].off = off;
        _rqes[slot].len = len;
        _rqTailLocal++;

        Volatile.Write(ref *_rqTail, _rqTailLocal);        
    }

    private static nuint AlignUp(nuint v, nuint a) => (v + (a - 1)) & ~(a - 1);

    public void Run()
    {
        Ring = Ring.Create(_ringEntries);
        _listenFd = OpenReusePortListener(_port);

        InitZcrx();

        Console.WriteLine($"[r{Id}] listening on 0.0.0.0:{_port} " +
                          $"(zcrx if_idx={_ifIdx} if_rxq={_ifRxq}, area={AreaLen >> 20} MiB)");
        SubmitAcceptMultishot();

        while (true)
        {
            int rc = Ring.SubmitAndWait(1);
            if (rc < 0 && rc != -4 /* EINTR */)
            {
                Console.Error.WriteLine($"[r{Id}] io_uring_enter failed: {rc}");
                break;
            }

            while (Ring.TryGetCqe(out IoUringCqe* cqe))
            {
                Dispatch(cqe);
                Ring.CqeSeen();
            }
        }

        close(_listenFd);
        Ring.Dispose();
    }

    private void Dispatch(IoUringCqe* cqe)
    {
        ulong kind = cqe->user_data & 0xffffffff_00000000UL;
        int   fd   = (int)(cqe->user_data & 0xffffffffUL);
        bool  more = (cqe->flags & IORING_CQE_F_MORE) != 0;

        if (kind == KindAccept)
        {
            if (cqe->res >= 0)
            {
                int clientFd = cqe->res;
                var conn = new Connection(this);
                Connections[clientFd] = conn;
                SubmitRecvZcMultishot(clientFd);

                _ = Handler.HandleAsync(this, clientFd, conn);
            }
            else
            {
                Console.Error.WriteLine($"[r{Id}] accept error: {cqe->res}");
            }
            // Multishot accept stays armed; only re-arm if the kernel terminated it.
            if (!more)
            {
                SubmitAcceptMultishot();
            }
        }
        else if (kind == KindRecv)
        {
            int res = cqe->res;

            if (!Connections.TryGetValue(fd, out var conn))
            {
                // No owner: if a chunk came with it, recycle it immediately.
                if (res > 0)
                {
                    IoUringZcrxCqe* rcqe0 = (IoUringZcrxCqe*)((byte*)cqe + 16);
                    RefillRqe(rcqe0->off, (uint)res);
                }
                return;
            }

            if (res <= 0)
            {
                // Error / EOF: no chunk attached.
                conn.Complete(res, 0, null);
                return;
            }

            // zcrx payload lives in the registered area; the trailing
            // io_uring_zcrx_cqe carries the (area_id << 48 | offset) token.
            IoUringZcrxCqe* rcqe = (IoUringZcrxCqe*)((byte*)cqe + 16);
            ulong off = rcqe->off;
            byte* ptr = _area + (off & ~IORING_ZCRX_AREA_MASK);

            conn.Complete(res, off, ptr);

            if (!more)
            {
                SubmitRecvZcMultishot(fd);
            }
        }
        else if (kind == KindSend)
        {
            if (Connections.TryGetValue(fd, out var conn) && cqe->res <= 0)
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
        sqe->user_data = KindAccept | (uint)_listenFd;
    }

    /// <summary>
    /// Multishot zero-copy recv. Binds to this ring's single registered zcrx
    /// ifq implicitly (no buffer-select, no buffer group). The kernel DMAs
    /// payload straight into the registered area and reports the location in
    /// the trailing io_uring_zcrx_cqe.
    /// </summary>
    public void SubmitRecvZcMultishot(int fd)
    {
        IoUringSqe* sqe = GetSqeOrFlush();
        Unsafe.InitBlockUnaligned(sqe, 0, 64);
        sqe->opcode    = IORING_OP_RECV_ZC;
        sqe->ioprio    = IORING_RECV_MULTISHOT;
        sqe->fd        = fd;
        sqe->user_data = KindRecv | (uint)fd;
    }

    public void SubmitSend(int fd, byte* buf, uint len)
    {
        IoUringSqe* sqe = GetSqeOrFlush();
        Unsafe.InitBlockUnaligned(sqe, 0, 64);
        sqe->opcode    = IORING_OP_SEND;
        sqe->fd        = fd;
        sqe->addr      = (ulong)buf;
        sqe->len       = len;
        sqe->user_data = KindSend | (uint)fd;
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
