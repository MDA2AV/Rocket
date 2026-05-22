using System.Runtime.CompilerServices;
using static Raptor.Native;

namespace Raptor;

/// <summary>
/// io_uring ring set up for MULTI-threaded submission. Unlike Minima's single-
/// issuer ring, Raptor's reactor and any threadpool thread (the per-connection
/// output pump) all write SQEs. So:
///   - SQ writes (GetSqe / PublishTail) are NOT thread-safe on their own; the
///     owning <see cref="RaptorReactor"/> serialises them with a lock.
///   - io_uring_enter itself IS safe to call concurrently from multiple threads
///     (the kernel takes its own submission lock), so Submit/Wait run outside
///     that lock to keep the critical section to just the ring-buffer writes.
///   - The CQ is drained only by the reactor thread (single consumer), no lock.
/// SINGLE_ISSUER / DEFER_TASKRUN are deliberately NOT set.
/// </summary>
public sealed unsafe class RaptorRing : IDisposable
{
    private int _fd;
    public int Fd => _fd;

    private uint*       _sqHead;
    private uint*       _sqTail;
    private uint*       _sqArray;
    private uint        _sqMask;
    private uint        _sqEntries;
    private IoUringSqe* _sqes;

    private uint*       _cqHead;
    private uint*       _cqTail;
    private IoUringCqe* _cqes;
    private uint        _cqMask;

    private uint _sqeTail;

    private byte* _ringPtr;
    private nuint _ringSize;
    private byte* _sqePtr;
    private nuint _sqeSize;

    public static RaptorRing Create(uint entries)
    {
        IoUringParams p = default;
        p.flags = 0;   // multi-submitter: no SINGLE_ISSUER, no DEFER_TASKRUN
        int fd = io_uring_setup(entries, &p);
        if (fd < 0)
            throw new InvalidOperationException($"io_uring_setup failed: {fd}");

        var ring = new RaptorRing { _fd = fd, _sqEntries = p.sq_entries };

        nuint sqRingBytes = p.sq_off.array + p.sq_entries * sizeof(uint);
        nuint cqRingBytes = p.cq_off.cqes  + p.cq_entries * (nuint)sizeof(IoUringCqe);
        nuint ringBytes   = sqRingBytes > cqRingBytes ? sqRingBytes : cqRingBytes;

        void* ringMem = mmap(null, ringBytes, PROT_READ | PROT_WRITE, MAP_SHARED | MAP_POPULATE, fd, IORING_OFF_SQ_RING);
        if (ringMem == (void*)-1) { close(fd); throw new InvalidOperationException("mmap(SQ_RING) failed"); }
        ring._ringPtr = (byte*)ringMem;
        ring._ringSize = ringBytes;

        nuint sqeBytes = p.sq_entries * (nuint)sizeof(IoUringSqe);
        void* sqeMem = mmap(null, sqeBytes, PROT_READ | PROT_WRITE, MAP_SHARED | MAP_POPULATE, fd, IORING_OFF_SQES);
        if (sqeMem == (void*)-1) { munmap(ringMem, ringBytes); close(fd); throw new InvalidOperationException("mmap(SQES) failed"); }
        ring._sqes = (IoUringSqe*)sqeMem;
        ring._sqePtr = (byte*)sqeMem;
        ring._sqeSize = sqeBytes;

        byte* b = (byte*)ringMem;
        ring._sqHead  = (uint*)(b + p.sq_off.head);
        ring._sqTail  = (uint*)(b + p.sq_off.tail);
        ring._sqArray = (uint*)(b + p.sq_off.array);
        ring._sqMask  = *(uint*)(b + p.sq_off.ring_mask);

        ring._cqHead = (uint*)(b + p.cq_off.head);
        ring._cqTail = (uint*)(b + p.cq_off.tail);
        ring._cqes   = (IoUringCqe*)(b + p.cq_off.cqes);
        ring._cqMask = *(uint*)(b + p.cq_off.ring_mask);

        return ring;
    }

    /// <summary>Claim an SQE slot. Caller MUST hold the reactor's SQ lock. Null if the SQ is full.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IoUringSqe* GetSqe()
    {
        uint head = Volatile.Read(ref *_sqHead);
        if (_sqeTail - head >= _sqEntries)
            return null;

        uint slot = _sqeTail & _sqMask;
        _sqArray[slot] = slot;
        _sqeTail++;
        return &_sqes[slot];
    }

    /// <summary>Publish queued SQEs to the kernel-visible tail. Caller MUST hold the SQ lock.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PublishTail() => Volatile.Write(ref *_sqTail, _sqeTail);

    /// <summary>Submit all published SQEs without waiting. Safe to call without the lock.</summary>
    public int Flush() => io_uring_enter(_fd, _sqEntries, 0, 0);

    /// <summary>Submit all published SQEs and block until at least one completion. No lock.</summary>
    public int SubmitAndWait() => io_uring_enter(_fd, _sqEntries, 1, IORING_ENTER_GETEVENTS);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint CqReady() => Volatile.Read(ref *_cqTail) - *_cqHead;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly IoUringCqe CqeAt(uint i) => ref _cqes[(*_cqHead + i) & _cqMask];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CqAdvance(uint n) => Volatile.Write(ref *_cqHead, *_cqHead + n);

    public void Dispose()
    {
        if (_ringPtr != null) { munmap(_ringPtr, _ringSize); _ringPtr = null; }
        if (_sqePtr  != null) { munmap(_sqePtr,  _sqeSize);  _sqePtr  = null; }
        if (_fd > 0) { close(_fd); _fd = 0; }
    }
}
