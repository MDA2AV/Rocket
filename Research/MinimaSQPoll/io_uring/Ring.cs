using System.Runtime.CompilerServices;
using static MinimaSQPoll.Native;

// ReSharper disable SuggestVarOrType_BuiltInTypes
// ReSharper disable SuggestVarOrType_Elsewhere
#pragma warning disable CA1806

namespace MinimaSQPoll;

public sealed unsafe class Ring : IDisposable 
{
    private int _fd;

    public int Fd => _fd;
    
    private uint*       _sqHead;
    private uint*       _sqTail;
    private uint*       _sqArray;
    private uint*       _sqFlags;     // kernel-shared SQ flags (carries IORING_SQ_NEED_WAKEUP under SQPOLL)
    private uint        _sqMask;
    private uint        _sqEntries;
    private IoUringSqe* _sqes;
    private bool        _sqPoll;
    
    private uint*       _cqHead;    
    private uint*       _cqTail;    
    private IoUringCqe* _cqes;
    private uint        _cqMask;

    private uint _sqeTail;

    // Guards SQE allocation + publish so any thread can submit. Critical section
    // is tiny (write 64 B, advance tail), so a SpinLock comfortably outperforms
    // Monitor and there is no scheduler interaction.
    private SpinLock _submitLock = new SpinLock(false);

    private byte* _ringPtr;
    private nuint _ringSize;
    private byte* _sqePtr;
    private nuint _sqeSize;
    
    public static Ring Create(uint entries, bool sqPoll = true, uint sqIdleMs = 1000, int sqCpu = -1)
    {
        IoUringParams ioUringParams = default;
        if (sqPoll)
        {
            // SQPOLL: kernel poller thread reads SQEs from shared memory and
            // submits them without us calling io_uring_enter. Incompatible with
            // SINGLE_ISSUER/DEFER_TASKRUN (the poller is the "submitter" from
            // the kernel's perspective).
            ioUringParams.flags = IORING_SETUP_SQPOLL;
            ioUringParams.sq_thread_idle = sqIdleMs;
            if (sqCpu >= 0)
            {
                ioUringParams.flags |= IORING_SETUP_SQ_AFF;
                ioUringParams.sq_thread_cpu = (uint)sqCpu;
            }
        }
        else
        {
            ioUringParams.flags = IORING_SETUP_SINGLE_ISSUER | IORING_SETUP_DEFER_TASKRUN;
        }
        int fd = io_uring_setup(entries, &ioUringParams);
        if (fd < 0)
        {
            throw new InvalidOperationException($"io_uring_setup failed: {fd}");
        }

        var ring = new Ring
        {
            _fd = fd,
            _sqEntries = ioUringParams.sq_entries,
            _sqPoll = sqPoll,
        };
        
        nuint sqRingBytes = ioUringParams.sq_off.array + ioUringParams.sq_entries * sizeof(uint);
        nuint cqRingBytes = ioUringParams.cq_off.cqes  + ioUringParams.cq_entries * (nuint)sizeof(IoUringCqe);
        nuint ringBytes   = sqRingBytes > cqRingBytes ? sqRingBytes : cqRingBytes;

        void* ringMem = mmap(null, ringBytes, PROT_READ | PROT_WRITE, MAP_SHARED | MAP_POPULATE, fd, IORING_OFF_SQ_RING);
        if (ringMem == (void*)-1)
        {
            close(fd); 
            
            throw new InvalidOperationException("mmap(SQ_RING) failed"); 
        }
        ring._ringPtr  = (byte*)ringMem;
        ring._ringSize = ringBytes;
        
        nuint sqeBytes = ioUringParams.sq_entries * (nuint)sizeof(IoUringSqe);
        void* sqeMem = mmap(null, sqeBytes, PROT_READ | PROT_WRITE, MAP_SHARED | MAP_POPULATE, fd, IORING_OFF_SQES);
        if (sqeMem == (void*)-1)
        {
            munmap(ringMem, ringBytes); 
            close(fd); 
            
            throw new InvalidOperationException("mmap(SQES) failed"); 
        }
        ring._sqes    = (IoUringSqe*)sqeMem;
        ring._sqePtr  = (byte*)sqeMem;
        ring._sqeSize = sqeBytes; 
        
        byte* ringPointer = (byte*)ringMem;
        ring._sqHead  = (uint*)(ringPointer + ioUringParams.sq_off.head);
        ring._sqTail  = (uint*)(ringPointer + ioUringParams.sq_off.tail);
        ring._sqArray = (uint*)(ringPointer + ioUringParams.sq_off.array);
        ring._sqFlags = (uint*)(ringPointer + ioUringParams.sq_off.flags);
        ring._sqMask  = *(uint*)(ringPointer + ioUringParams.sq_off.ring_mask);

        ring._cqHead = (uint*)(ringPointer + ioUringParams.cq_off.head);
        ring._cqTail = (uint*)(ringPointer + ioUringParams.cq_off.tail);
        ring._cqes   = (IoUringCqe*)(ringPointer + ioUringParams.cq_off.cqes);
        ring._cqMask = *(uint*)(ringPointer + ioUringParams.cq_off.ring_mask);

        return ring;
    }
    
    // Thread-safe SQE allocation. The lock is held until PublishSqe() is called,
    // so callers must always call PublishSqe (or be on the reactor with no other
    // threads submitting). Pattern is: sqe = TryGetSqe(); write fields; PublishSqe().
    public IoUringSqe* TryGetSqe()
    {
        bool taken = false;
        _submitLock.Enter(ref taken);

        uint head = Volatile.Read(ref *_sqHead);
        if (_sqeTail - head >= _sqEntries)
        {
            _submitLock.Exit();
            return null;
        }

        uint slot = _sqeTail & _sqMask;
        _sqArray[slot] = slot;
        _sqeTail++;

        return &_sqes[slot];
    }

    // Publishes the SQE the caller just wrote to the kernel-visible tail and
    // releases the submit lock. Under SQPOLL this also wakes the poller if it
    // has parked (SQ_NEED_WAKEUP set).
    public void PublishSqe()
    {
        Volatile.Write(ref *_sqTail, _sqeTail);

        if (_sqPoll && (Volatile.Read(ref *_sqFlags) & IORING_SQ_NEED_WAKEUP) != 0)
        {
            io_uring_enter(_fd, 0, 0, IORING_ENTER_SQ_WAKEUP);
        }

        _submitLock.Exit();
    }
    
    // Block waiting for at least waitFor CQEs. With direct submission (handlers
    // call TryGetSqe/PublishSqe), the reactor only ever needs to wait here —
    // no submit work to coordinate. Under non-SQPOLL we still need to submit
    // any pending SQEs along with the wait.
    public int WaitForCqe(uint waitFor)
    {
        if (_sqPoll)
        {
            if (waitFor == 0) return 0;
            return io_uring_enter(_fd, 0, waitFor, IORING_ENTER_GETEVENTS);
        }

        // Non-SQPOLL fallback: submit + wait in one syscall.
        uint published = *_sqTail;
        uint toSubmit  = _sqeTail - published;
        if (toSubmit > 0)
        {
            Volatile.Write(ref *_sqTail, _sqeTail);
        }
        if (toSubmit == 0 && waitFor == 0) return 0;
        uint enterFlags = waitFor > 0 ? IORING_ENTER_GETEVENTS : 0;
        return io_uring_enter(_fd, toSubmit, waitFor, enterFlags);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetCqe(out IoUringCqe cqe) 
    {
        uint head = *_cqHead;
        uint tail = Volatile.Read(ref *_cqTail);

        if (head == tail)
        {
            cqe = default; 
            
            return false; 
        }

        cqe = _cqes[head & _cqMask];
        
        return true;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CqeSeen() => Volatile.Write(ref *_cqHead, *_cqHead + 1);

    // Batched CQ drain (liburing io_uring_for_each_cqe + io_uring_cq_advance):
    // read the kernel-written tail once (acquire), process the whole batch,
    // then publish the consumed head once (release) instead of once per CQE.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint CqReady() => Volatile.Read(ref *_cqTail) - *_cqHead;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly IoUringCqe CqeAt(uint i) => ref _cqes[(*_cqHead + i) & _cqMask];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CqAdvance(uint n) => Volatile.Write(ref *_cqHead, *_cqHead + n);

    public void Dispose()
    {
        if (_ringPtr != null)
        {
            munmap(_ringPtr, _ringSize); _ringPtr = null; 
        }

        if (_sqePtr != null)
        {
            munmap(_sqePtr,  _sqeSize);  _sqePtr  = null; 
        }

        if (_fd > 0)
        {
            close(_fd); _fd = 0; 
        }
    }
}

#pragma warning restore CA1806
