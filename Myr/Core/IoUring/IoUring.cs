namespace Myr.Core.IoUring;

internal sealed unsafe class IoUring : IDisposable
{
    // Ring file descriptor
    private int _ringFd;

    // SQ pointers (into mmap'd region)
    private uint* _sqHead;
    private uint* _sqTail;
    private uint* _sqFlags;
    private uint* _sqArray;

    // CQ pointers (into mmap'd region)
    private uint* _cqTail;
    private IoUringCqe* _cqCqes;

    // SQE array (separate mmap)
    private IoUringSqe* _sqes;

    // Cached immutable values (avoid pointer deref on hot path)
    private uint _sqMaskVal;
    private uint _cqMaskVal;

    // Local SQE tracking
    private uint _sqeHead;
    private uint _sqeTail;
    private uint _sqEntries;

    // Local CQ head (written back to mmap on CqAdvance)
    private uint _cqHead;
    private uint* _cqHeadPtr;

    // Mmap bookkeeping for cleanup
    private byte* _sqRingPtr;
    private nuint _sqRingSize;
    private byte* _sqePtr;
    private nuint _sqeSize;

    // Setup flags (for external queries)
    public uint SetupFlags { get; private set; }

    /// <summary>
    /// Creates a new io_uring instance.
    /// </summary>
    public static IoUring Create(uint entries, uint flags = 0, int sqCpu = -1, uint sqIdleMs = 100)
    {
        var ring = new IoUring();

        IoUringParams p = default;
        p.flags = flags;

        if ((flags & IORING_SETUP_SQPOLL) != 0)
        {
            if (sqCpu >= 0)
            {
                p.sq_thread_cpu = (uint)sqCpu;
                p.flags |= IORING_SETUP_SQ_AFF;
            }
            p.sq_thread_idle = sqIdleMs;
        }

        int fd = io_uring_setup(entries, &p);
        if (fd < 0)
            throw new InvalidOperationException($"io_uring_setup failed: {fd}");

        ring._ringFd = fd;
        ring.SetupFlags = p.flags;
        ring._sqEntries = p.sq_entries;

        // Compute mmap sizes — SINGLE_MMAP: SQ and CQ share one region
        nuint sqRingSize = (nuint)(p.sq_off.array + p.sq_entries * sizeof(uint));
        nuint cqRingSize = (nuint)(p.cq_off.cqes + p.cq_entries * (nuint)sizeof(IoUringCqe));
        nuint ringSize = sqRingSize > cqRingSize ? sqRingSize : cqRingSize;

        // mmap SQ+CQ ring
        void* sqRingMem = mmap(
            null, ringSize,
            PROT_READ | PROT_WRITE,
            MAP_SHARED | MAP_POPULATE,
            fd, IORING_OFF_SQ_RING);

        if (sqRingMem == (void*)-1)
        {
            close(fd);
            throw new InvalidOperationException("mmap(SQ_RING) failed");
        }

        ring._sqRingPtr = (byte*)sqRingMem;
        ring._sqRingSize = ringSize;

        // mmap SQE array
        nuint sqeSize = (nuint)(p.sq_entries * (nuint)sizeof(IoUringSqe));
        void* sqeMem = mmap(
            null, sqeSize,
            PROT_READ | PROT_WRITE,
            MAP_SHARED | MAP_POPULATE,
            fd, IORING_OFF_SQES);

        if (sqeMem == (void*)-1)
        {
            munmap(sqRingMem, ringSize);
            close(fd);
            throw new InvalidOperationException("mmap(SQES) failed");
        }

        ring._sqePtr = (byte*)sqeMem;
        ring._sqeSize = sqeSize;
        ring._sqes = (IoUringSqe*)sqeMem;

        // Initialize field pointers from params offsets
        byte* sq = (byte*)sqRingMem;
        ring._sqHead  = (uint*)(sq + p.sq_off.head);
        ring._sqTail  = (uint*)(sq + p.sq_off.tail);
        ring._sqFlags = (uint*)(sq + p.sq_off.flags);
        ring._sqArray = (uint*)(sq + p.sq_off.array);

        // Cache immutable mask values (never change after setup)
        ring._sqMaskVal = *(uint*)(sq + p.sq_off.ring_mask);
        ring._cqMaskVal = *(uint*)((byte*)sqRingMem + p.cq_off.ring_mask);

        // CQ pointers
        byte* cq = (byte*)sqRingMem;
        ring._cqHeadPtr = (uint*)(cq + p.cq_off.head);
        ring._cqTail    = (uint*)(cq + p.cq_off.tail);
        ring._cqCqes    = (IoUringCqe*)(cq + p.cq_off.cqes);

        // Cache the initial CQ head locally
        ring._cqHead = *ring._cqHeadPtr;

        ring._sqeHead = 0;
        ring._sqeTail = 0;

        return ring;
    }

    /// <summary>
    /// Get the next SQE slot. Returns null if the SQ is full.
    /// Does NOT zero the SQE — prep helpers are responsible for setting all required fields.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IoUringSqe* GetSqe()
    {
        uint next = _sqeTail + 1;

        if (next - _sqeHead > _sqEntries)
            return null;

        IoUringSqe* sqe = &_sqes[_sqeTail & _sqMaskVal];
        _sqeTail = next;
        return sqe;
    }

    /// <summary>
    /// Copies SQE indices to the SQ array and publishes the tail.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint Flush()
    {
        uint toSubmit = _sqeTail - _sqeHead;

        if (toSubmit == 0)
            return 0;

        uint mask = _sqMaskVal;
        uint tail = *_sqTail;
        uint head = _sqeHead;

        for (uint i = 0; i < toSubmit; i++)
        {
            _sqArray[(tail + i) & mask] = (head + i) & mask;
        }

        _sqeHead = _sqeTail;

        Volatile.Write(ref *_sqTail, tail + toSubmit);

        return toSubmit;
    }

    /// <summary>
    /// Submit pending SQEs to the kernel.
    /// </summary>
    public int Submit()
    {
        uint toSubmit = Flush();
        if (toSubmit == 0)
            return 0;

        return io_uring_enter(_ringFd, toSubmit, 0, 0, null, 0);
    }

    /// <summary>
    /// Flush + submit + wait for at least waitNr CQEs with optional timeout.
    /// </summary>
    public int SubmitAndWaitTimeout(uint waitNr, KernelTimespec* ts)
    {
        uint toSubmit = Flush();

        IoUringGetEventsArg arg;
        arg.sigmask = 0;
        arg.sigmask_sz = 0;
        arg.pad = 0;
        arg.ts = (ulong)ts;

        return io_uring_enter(
            _ringFd, toSubmit, waitNr,
            IORING_ENTER_GETEVENTS | IORING_ENTER_EXT_ARG,
            &arg, (nuint)sizeof(IoUringGetEventsArg));
    }

    /// <summary>
    /// Non-blocking peek for up to count CQEs.
    /// Returns number of CQEs available.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int PeekBatchCqe(IoUringCqe** cqes, int count)
    {
        uint head = _cqHead;
        uint tail = Volatile.Read(ref *_cqTail);

        int available = (int)(tail - head);
        if (available <= 0)
            return 0;

        if (available > count)
            available = count;

        uint mask = _cqMaskVal;
        for (int i = 0; i < available; i++)
        {
            cqes[i] = &_cqCqes[(head + (uint)i) & mask];
        }

        return available;
    }

    /// <summary>
    /// Advance the CQ head by count entries, marking CQEs as consumed.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CqAdvance(uint count)
    {
        _cqHead += count;
        Volatile.Write(ref *_cqHeadPtr, _cqHead);
    }

    /// <summary>
    /// Mark a single CQE as seen.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CqeSeen(IoUringCqe* cqe)
    {
        CqAdvance(1);
    }

    /// <summary>
    /// Number of SQEs ready to be submitted.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint SqReady()
    {
        return _sqeTail - _sqeHead;
    }

    /// <summary>
    /// Number of CQEs ready to be consumed.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint CqReady()
    {
        return Volatile.Read(ref *_cqTail) - _cqHead;
    }

    /// <summary>
    /// Ring file descriptor (for registration operations).
    /// </summary>
    public int Fd => _ringFd;

    public void Dispose()
    {
        if (_sqRingPtr != null)
        {
            munmap(_sqRingPtr, _sqRingSize);
            _sqRingPtr = null;
        }

        if (_sqePtr != null)
        {
            munmap(_sqePtr, _sqeSize);
            _sqePtr = null;
        }

        if (_ringFd >= 0)
        {
            close(_ringFd);
            _ringFd = -1;
        }
    }
}