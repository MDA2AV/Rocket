using System.Runtime.InteropServices;
using System.Threading.Tasks.Sources;
// ReSharper disable SuggestVarOrType_BuiltInTypes

namespace Minima;

internal readonly struct RecvSnapshot
{
    public readonly long Tail;
    public readonly bool IsClosed;

    public RecvSnapshot(long tail, bool isClosed)
    {
        Tail = tail;
        IsClosed = isClosed;
    }

    public static RecvSnapshot Closed() => new(0, isClosed: true);
}

/// <summary>
/// Per-connection state. The handler may run on any thread (e.g. resumed by
/// a thread-pool timer); reactor-only side effects are funnelled through the
/// MPSC queues on `Reactor`. Coordination uses Interlocked.Exchange on the
/// arm flags and a sticky `_pending` to close the lost-wakeup race.
/// </summary>
internal sealed unsafe class Connection : IValueTaskSource<RecvSnapshot>, IValueTaskSource
{
    private readonly Reactor _reactor;

    public int ClientFd { get; }

    // =========================================================================
    // Per-connection write slab. The handler is the only writer (WriteTail);
    // the reactor is the only updater of WriteHead. The MPSC handoff in
    // FlushAsync → DrainFlushQ provides the happens-before edge.
    // =========================================================================
    private readonly int _writeSlabSize;
    internal byte* WriteBuffer;
    internal int   WriteHead;
    internal int   WriteTail;
    internal int   WriteInFlight;

    public Connection(Reactor reactor, int fd, int writeSlabSize = 1024 * 16)
    {
        _reactor = reactor;
        ClientFd = fd;
        _writeSlabSize = writeSlabSize;
        WriteBuffer = (byte*)NativeMemory.AlignedAlloc((nuint)writeSlabSize, 64);
    }

    // =========================================================================
    // Read state
    // =========================================================================

    private ManualResetValueTaskSourceCore<RecvSnapshot> _readSignal;
    private int _armed;     // 0 = not parked, 1 = handler suspended on _readSignal
    private int _pending;   // sticky "wake owed" — producer sets when consumer
                            //   wasn't yet armed but data arrived
    private int _closed;

    private readonly SpscRecvRing _recv = new(capacityPow2: 16);

    public ValueTask<RecvSnapshot> ReadAsync()
    {
        // Fast path: data already buffered, OR a wake is owed from a producer
        // that ran while we weren't yet armed.
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

        // Arm. Interlocked.Exchange is the linearization point against the
        // producer's symmetric Exchange in Complete/MarkClosed.
        if (Interlocked.Exchange(ref _armed, 1) == 1)
        {
            throw new InvalidOperationException("ReadAsync already armed.");
        }

        // Race recovery: re-check between arming and returning the IVTS task.
        // Catches a producer that ran in the window after our fast-path check
        // and before our arm, set _pending=1, and went on its way.
        if (!_recv.IsEmpty() || Volatile.Read(ref _pending) == 1 || Volatile.Read(ref _closed) != 0)
        {
            Volatile.Write(ref _pending, 0);
            Interlocked.Exchange(ref _armed, 0);
            return new ValueTask<RecvSnapshot>(
                new RecvSnapshot(_recv.SnapshotTail(), Volatile.Read(ref _closed) != 0));
        }

        return new ValueTask<RecvSnapshot>(this, _readSignal.Version);
    }

    public bool TryGetItem(in RecvSnapshot snap, out SpscRecvRing.Item item)
        => _recv.TryDequeueUntil(snap.Tail, out item);

    public void ResetRead() => _readSignal.Reset();

    // Called by the reactor on recv res > 0. The res <= 0 path lives in
    // Reactor.Dispatch directly (teardown is reactor-owned).
    public void Complete(int res, ushort bid, bool hasBuffer, byte* ptr)
    {
        if (!_recv.TryEnqueue(new SpscRecvRing.Item
                 {
                     Ptr = ptr,
                     Bid = bid,
                     Len = res,
                     HasBuffer = hasBuffer
                 }))
        {
            Console.Error.WriteLine("[conn] recv queue overflow.");
            if (hasBuffer)
            {
                _reactor.ReturnBufferDirect(bid);
            }
            Volatile.Write(ref _closed, 1);
        }

        // Symmetric pair to ReadAsync's arm + re-check: either wake the parked
        // reader (we win the Exchange), or leave a sticky note (consumer will
        // see it on its next ReadAsync, including the race-recovery branch).
        if (Interlocked.Exchange(ref _armed, 0) == 1)
        {
            _readSignal.SetResult(new RecvSnapshot(_recv.SnapshotTail(), Volatile.Read(ref _closed) != 0));
        }
        else
        {
            Volatile.Write(ref _pending, 1);
        }
    }

    // Called by reactor when the connection is being torn down (recv or send
    // error path). Signals any awaiters cleanly; does not free WriteBuffer
    // (that's FreeNative's job, called separately).
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

        // Complete any pending flush as a (silent) success so the handler
        // exits via the next ReadAsync's IsClosed branch rather than throwing.
        if (Interlocked.Exchange(ref _flushArmed, 0) == 1)
        {
            Volatile.Write(ref _flushInProgress, 0);
            _flushSignal.SetResult(true);
        }
    }

    // =========================================================================
    // Write API
    // =========================================================================

    private ManualResetValueTaskSourceCore<bool> _flushSignal = new()
    {
        RunContinuationsAsynchronously = false,
    };
    private int _flushArmed;
    private int _flushInProgress;

    public void Write(ReadOnlySpan<byte> source)
    {
        if (Volatile.Read(ref _flushInProgress) != 0)
            throw new InvalidOperationException("Cannot write while flush is in progress.");

        int len = source.Length;
        if (WriteTail + len > _writeSlabSize)
            throw new InvalidOperationException("Write buffer too small.");

        source.CopyTo(new Span<byte>(WriteBuffer + WriteTail, len));
        WriteTail += len;
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        if (Volatile.Read(ref _flushInProgress) != 0)
            throw new InvalidOperationException("Cannot write while flush is in progress.");

        if (WriteTail + sizeHint > _writeSlabSize)
            throw new InvalidOperationException("Write buffer too small.");

        return new Span<byte>(WriteBuffer + WriteTail, _writeSlabSize - WriteTail);
    }

    public void Advance(int count)
    {
        if (Volatile.Read(ref _flushInProgress) != 0)
            throw new InvalidOperationException("Cannot write while flush is in progress.");

        WriteTail += count;
    }

    public ValueTask FlushAsync()
    {
        if (Interlocked.Exchange(ref _flushInProgress, 1) == 1)
            throw new InvalidOperationException("FlushAsync already in progress.");

        int target = WriteTail;
        if (target == 0)
        {
            Volatile.Write(ref _flushInProgress, 0);
            return default;
        }

        if (Interlocked.Exchange(ref _flushArmed, 1) == 1)
            throw new InvalidOperationException("FlushAsync already armed.");

        _flushSignal.Reset();
        WriteInFlight = target;

        // Cross-thread safe: hand the fd off to the reactor's flush queue.
        // The reactor is the sole writer of SQEs.
        _reactor.EnqueueFlush(ClientFd);

        return new ValueTask(this, _flushSignal.Version);
    }

    // Called by reactor on the final send-completion of a flush target.
    internal void CompleteFlush()
    {
        WriteHead = 0;
        WriteTail = 0;
        WriteInFlight = 0;
        Volatile.Write(ref _flushInProgress, 0);
        Interlocked.Exchange(ref _flushArmed, 0);
        _flushSignal.SetResult(true);
    }

    // =========================================================================
    // Teardown — invoked from Reactor.Dispatch's recv/send error paths.
    // Reactor-thread only; runs after MarkClosed has woken awaiters.
    // =========================================================================

    internal void FreeNative()
    {
        // Drain any leftover SPSC items, returning their buffers.
        while (_recv.TryDequeue(out SpscRecvRing.Item item))
        {
            if (item.HasBuffer)
            {
                _reactor.ReturnBufferDirect(item.Bid);
            }
        }

        if (WriteBuffer != null)
        {
            NativeMemory.AlignedFree(WriteBuffer);
            WriteBuffer = null;
        }
    }

    // =========================================================================
    // IValueTaskSource plumbing — two separate signals share this object,
    // dispatched via the generic vs non-generic interface entries.
    // =========================================================================

    RecvSnapshot IValueTaskSource<RecvSnapshot>.GetResult(short token) => _readSignal.GetResult(token);

    ValueTaskSourceStatus IValueTaskSource<RecvSnapshot>.GetStatus(short token) => _readSignal.GetStatus(token);

    void IValueTaskSource<RecvSnapshot>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        => _readSignal.OnCompleted(continuation, state, token, flags);

    void IValueTaskSource.GetResult(short token) => _flushSignal.GetResult(token);

    ValueTaskSourceStatus IValueTaskSource.GetStatus(short token) => _flushSignal.GetStatus(token);

    void IValueTaskSource.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        => _flushSignal.OnCompleted(continuation, state, token, flags);
}
