using System.Threading.Tasks.Sources;
using ioxide.utils;

// ReSharper disable SuggestVarOrType_BuiltInTypes

namespace ioxide;

/// <summary>
/// The transport-neutral heart of a connection: the inline-resume read surface (an
/// <see cref="IValueTaskSource{T}"/> over a recv queue), the pool generation that invalidates stale
/// awaiters, and the two-owner refcount. <see cref="TcpConnection"/> extends it with the stream
/// socket's buffer-ring recv, write slab, and flush; a QUIC engine connection will extend it the same
/// way, feeding the queue from decrypted stream data. All the fiddly IVTS race handling lives here
/// once so both transports share it.
///
/// The handler may run on any thread; coordination uses Interlocked on the arm flag plus a sticky
/// _pending to close the lost-wakeup race. _generation invalidates awaiters from a previous life.
/// </summary>
public abstract unsafe class Connection : IValueTaskSource<RecvSnapshot>
{
    protected readonly Reactor _reactor;
    private protected readonly SpscRecvRing _recv;   // sized by ServerConfig.RecvQueueEntries

    private ManualResetValueTaskSourceCore<RecvSnapshot> _readSignal = new()
    {
        RunContinuationsAsynchronously = false,
    };
    private int _armed;
    private int _pending;
    private protected int _closed;

    // Bumped on Clear(); the low 16 bits serve as the IVTS token so stale awaiters from a previous
    // pool life are detectable.
    private protected int _generation;

    // Two owners: the reactor (recv side) and the handler. Init 2 on accept; teardown runs only at
    // 0, so a connection is never recycled under a live handler.
    private int _refs;

    // Guards the framework's fault-path release of the handler ref; reset per pool life (#94).
    private int _handlerRefReleased;

    protected Connection(Reactor reactor, int recvQueueEntries)
    {
        _reactor = reactor;
        _recv = new SpscRecvRing(recvQueueEntries);
    }

    internal int Generation => Volatile.Read(ref _generation);

    public ValueTask<RecvSnapshot> ReadAsync()
    {
        if (!_recv.IsEmpty() || Volatile.Read(ref _pending) == 1)
        {
            Volatile.Write(ref _pending, 0);
            return new ValueTask<RecvSnapshot>(
                new RecvSnapshot(_recv.SnapshotTail(), Volatile.Read(ref _closed) != 0));
        }

        if (Volatile.Read(ref _closed) != 0)
        {
            return new ValueTask<RecvSnapshot>(RecvSnapshot.Closed());
        }

        if (Interlocked.Exchange(ref _armed, 1) == 1)
        {
            throw new InvalidOperationException("ReadAsync already armed.");
        }

        // Generation is the IVTS token: a Clear() during pool recycle invalidates this awaiter.
        int gen = Volatile.Read(ref _generation);

        // Race recovery: re-check between arming and returning the task.
        if (!_recv.IsEmpty() || Volatile.Read(ref _pending) == 1 || Volatile.Read(ref _closed) != 0)
        {
            Volatile.Write(ref _pending, 0);
            Interlocked.Exchange(ref _armed, 0);

            return new ValueTask<RecvSnapshot>(
                new RecvSnapshot(_recv.SnapshotTail(), Volatile.Read(ref _closed) != 0));
        }

        return new ValueTask<RecvSnapshot>(this, (short)gen);
    }

    public bool TryGetItem(in RecvSnapshot snap, out SpscRecvRing.Item item)
        => _recv.TryDequeueUntil(snap.Tail, out item);

    /// <summary>
    /// Drain the snapshot into one array of memory views - the multi-item way to reconstruct a
    /// request that arrived fragmented. Build a sequence with ToReadOnlySequence(), parse, then
    /// hand everything back with <see cref="TcpConnection.ReturnBuffers"/>. Allocates one array per
    /// call; the raw TryGetItem loop and ConnectionPipeReader remain the allocation-free paths.
    /// </summary>
    public UnmanagedMemoryManager[] GetSnapshotMemories(in RecvSnapshot snap)
    {
        int count = _recv.CountUntil(snap.Tail);
        if (count == 0)
        {
            return [];
        }

        var memories = new UnmanagedMemoryManager[count];
        int n = 0;
        while (n < count && _recv.TryDequeueUntil(snap.Tail, out SpscRecvRing.Item item))
        {
            if (item.HasBuffer)
            {
                memories[n++] = item.AsMemoryManager();
            }
        }

        if (n == count)
        {
            return memories;
        }
        Array.Resize(ref memories, n);   // rare: an item carried no buffer
        return memories;
    }

    public void ResetRead() => _readSignal.Reset();

    // Signal that data landed in the recv queue: wake the armed reader inline, or leave a sticky
    // _pending for the next ReadAsync. The enqueue itself is the transport's job (TcpConnection.Complete
    // for the buffer ring; the QUIC engine for decrypted stream bytes).
    private protected void SignalDataArrived()
    {
        if (Interlocked.Exchange(ref _armed, 0) == 1)
        {
            _readSignal.SetResult(new RecvSnapshot(_recv.SnapshotTail(), Volatile.Read(ref _closed) != 0));
        }
        else
        {
            Volatile.Write(ref _pending, 1);
        }
    }

    // Wake awaiters with closed=1. Reactor-thread or teardown paths only. OnMarkClosed lets the
    // transport tear down its own signals (e.g. TcpConnection's flush).
    public void MarkClosed()
    {
        Volatile.Write(ref _closed, 1);

        if (Interlocked.Exchange(ref _armed, 0) == 1)
        {
            _readSignal.SetResult(new RecvSnapshot(_recv.SnapshotTail(), isClosed: true));
        }
        else
        {
            Volatile.Write(ref _pending, 1);
        }

        OnMarkClosed();
    }

    internal void InitRefs() => Volatile.Write(ref _refs, 2);

    /// <summary>Release one owner's ref; whoever hits 0 hands the connection to the reactor for recycle.</summary>
    public void DecRef()
    {
        if (Interlocked.Decrement(ref _refs) == 0)
        {
            EnqueueForRecycle();
        }
    }

    /// <summary>
    /// Release the handler's ref on behalf of a handler that faulted before its own DecRef, so the
    /// fd/slab/gid aren't leaked (#94). Generation-guarded - a fault observed after this connection
    /// was recycled must not touch its next life - and idempotent within a life. A handler that
    /// DecRefs and then throws is outside the refcount contract; the guards keep that from
    /// corrupting a reused connection.
    /// </summary>
    internal void ReleaseHandlerRefOnFault(int generationAtStart)
    {
        if (Volatile.Read(ref _generation) == generationAtStart &&
            Interlocked.Exchange(ref _handlerRefReleased, 1) == 0)
        {
            DecRef();
        }
    }

    // Reset for pool reuse. Bump generation FIRST so stale IVTS tokens resolve to Closed()/no-op,
    // then clear the read/refcount state; OnClear lets the transport reset its own (write slab,
    // flush signal, buffer-ring mode).
    internal void Clear()
    {
        Interlocked.Increment(ref _generation);

        Volatile.Write(ref _armed, 0);
        Volatile.Write(ref _pending, 0);
        Volatile.Write(ref _closed, 0);
        Volatile.Write(ref _handlerRefReleased, 0);

        _readSignal.Reset();
        _recv.Reset();

        OnClear();
    }

    // --- transport hooks ---

    /// <summary>Hand this connection to the reactor for recycle once its refcount hits zero.</summary>
    protected abstract void EnqueueForRecycle();

    /// <summary>Tear down the transport's own close-time signals (default: none).</summary>
    protected virtual void OnMarkClosed() { }

    /// <summary>Reset the transport's own pooled state (default: none).</summary>
    protected virtual void OnClear() { }

    // --- IValueTaskSource<RecvSnapshot>: token = generation snapshot (cross-life guard); the core's
    //     own Version is passed through for the mid-life dispatch. ---

    RecvSnapshot IValueTaskSource<RecvSnapshot>.GetResult(short token)
    {
        if (token != (short)Volatile.Read(ref _generation))
        {
            return RecvSnapshot.Closed();
        }

        return _readSignal.GetResult(_readSignal.Version);
    }

    ValueTaskSourceStatus IValueTaskSource<RecvSnapshot>.GetStatus(short token)
    {
        if (token != (short)Volatile.Read(ref _generation))
        {
            return ValueTaskSourceStatus.Succeeded;
        }

        return _readSignal.GetStatus(_readSignal.Version);
    }

    void IValueTaskSource<RecvSnapshot>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
    {
        if (token != (short)Volatile.Read(ref _generation))
        {
            // Stale - unblock now; GetResult returns Closed().
            continuation(state);

            return;
        }

        // This source only completes on the owning reactor thread, so the continuation already runs
        // where ReactorSynchronizationContext would post it. Strip the scheduling-context flag or
        // MRVTSC posts every resume to the mailbox instead of invoking it inline
        // (RunContinuationsAsynchronously=false only covers the null-context case).
        _readSignal.OnCompleted(continuation, state, _readSignal.Version,
            flags & ~ValueTaskSourceOnCompletedFlags.UseSchedulingContext);
    }
}
