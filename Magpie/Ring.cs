using System.Runtime.CompilerServices;
using static Magpie.Native;

namespace Magpie;

/// <summary>
/// A single io_uring ring used for one read at a time. Plain ring — no SINGLE_ISSUER
/// or DEFER_TASKRUN, because the pool hands the same ring to different threads over
/// its lifetime (never two at once, so single-consumer CQ semantics hold). Optionally
/// attaches its io-wq to a primary ring so the pool shares one worker backend.
/// </summary>
public sealed unsafe class Ring : IDisposable
{
    private int _fd;
    public int Fd => _fd;

    private uint*       _sqTail;
    private uint*       _sqArray;
    private uint        _sqMask;
    private IoUringSqe* _sqes;

    private uint*       _cqHead;
    private uint*       _cqTail;
    private uint        _cqMask;
    private IoUringCqe* _cqes;

    private byte* _ringPtr; private nuint _ringSize;
    private byte* _sqePtr;  private nuint _sqeSize;

    public static Ring Create(uint entries, int wqFd)
    {
        IoUringParams p = default;
        if (wqFd >= 0)
        {
            p.flags = IORING_SETUP_ATTACH_WQ;
            p.wq_fd = (uint)wqFd;
        }

        int fd = io_uring_setup(entries, &p);
        if (fd < 0) throw new InvalidOperationException($"io_uring_setup failed: {fd}");

        var ring = new Ring { _fd = fd };

        nuint sqRingBytes = p.sq_off.array + p.sq_entries * sizeof(uint);
        nuint cqRingBytes = p.cq_off.cqes  + p.cq_entries * (nuint)sizeof(IoUringCqe);
        nuint ringBytes   = sqRingBytes > cqRingBytes ? sqRingBytes : cqRingBytes;

        void* rm = mmap(null, ringBytes, PROT_READ | PROT_WRITE, MAP_SHARED | MAP_POPULATE, fd, IORING_OFF_SQ_RING);
        if (rm == (void*)-1) { close(fd); throw new InvalidOperationException("mmap(SQ_RING) failed"); }
        ring._ringPtr = (byte*)rm; ring._ringSize = ringBytes;

        nuint sqeBytes = p.sq_entries * (nuint)sizeof(IoUringSqe);
        void* sm = mmap(null, sqeBytes, PROT_READ | PROT_WRITE, MAP_SHARED | MAP_POPULATE, fd, IORING_OFF_SQES);
        if (sm == (void*)-1) { munmap(rm, ringBytes); close(fd); throw new InvalidOperationException("mmap(SQES) failed"); }
        ring._sqes = (IoUringSqe*)sm; ring._sqePtr = (byte*)sm; ring._sqeSize = sqeBytes;

        byte* b = (byte*)rm;
        ring._sqTail  = (uint*)(b + p.sq_off.tail);
        ring._sqArray = (uint*)(b + p.sq_off.array);
        ring._sqMask  = *(uint*)(b + p.sq_off.ring_mask);
        ring._cqHead  = (uint*)(b + p.cq_off.head);
        ring._cqTail  = (uint*)(b + p.cq_off.tail);
        ring._cqMask  = *(uint*)(b + p.cq_off.ring_mask);
        ring._cqes    = (IoUringCqe*)(b + p.cq_off.cqes);
        return ring;
    }

    /// <summary>
    /// Submit one READ and return its result: bytes read (0 = EOF) or a negative errno.
    /// Inline on a page-cache hit (the submit enter completes it and we peek it straight
    /// out of the CQ — no wait); blocking on a miss (io_uring punts the read to an io-wq
    /// worker and we wait for its CQE). Caller must hold this ring exclusively.
    /// </summary>
    public int SubmitRead(int fd, byte* buf, uint len, long offset)
    {
        uint tail  = *_sqTail;
        uint index = tail & _sqMask;
        IoUringSqe* sqe = &_sqes[index];
        Unsafe.InitBlockUnaligned(sqe, 0, 64);
        sqe->opcode    = IORING_OP_READ;
        sqe->fd        = fd;
        sqe->off       = (ulong)offset;
        sqe->addr      = (ulong)buf;
        sqe->len       = len;
        sqe->user_data = 1;
        _sqArray[index] = index;
        Volatile.Write(ref *_sqTail, tail + 1);

        int rc = io_uring_enter(_fd, 1, 0, 0);   // submit, don't wait
        if (rc < 0) return rc;

        if (TryReap(out int res)) return res;    // cache hit: completed inline

        rc = io_uring_enter(_fd, 0, 1, IORING_ENTER_GETEVENTS);  // miss: wait for io-wq
        if (rc < 0) return rc;
        TryReap(out res);
        return res;
    }

    private bool TryReap(out int res)
    {
        uint head = *_cqHead;
        uint tail = Volatile.Read(ref *_cqTail);
        if (head == tail) { res = 0; return false; }
        res = _cqes[head & _cqMask].res;
        Volatile.Write(ref *_cqHead, head + 1);
        return true;
    }

    public void Dispose()
    {
        if (_ringPtr != null) { munmap(_ringPtr, _ringSize); _ringPtr = null; }
        if (_sqePtr  != null) { munmap(_sqePtr, _sqeSize);   _sqePtr  = null; }
        if (_fd > 0) { close(_fd); _fd = 0; }
    }
}
