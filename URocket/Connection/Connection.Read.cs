using System.Runtime.CompilerServices;
using System.Threading.Tasks.Sources;
using URocket.Utils;
using URocket.Utils.MultiProducerSingleConsumer;
using URocket.Utils.UnmanagedMemoryManager;
using ReadResult = URocket.Utils.ReadResult;

namespace URocket.Connection;

[SkipLocalsInit]
public sealed unsafe partial class Connection : IValueTaskSource<ReadResult>
{
    public int ClientFd { get; private set; }
    public Engine.Engine.Reactor Reactor { get; private set; } = null!;

    // Read completion primitive
    private ManualResetValueTaskSourceCore<ReadResult> _readSignal;

    // 0/1 atomic flags
    private int _armed;   // there is a waiter that must be woken
    private int _pending; // data arrived while not armed (edge)

    // Connection lifetime / pooling safety
    private int _closed;      // 0=open, 1=closed (published)
    private int _generation;  // incremented on Clear()/reuse

    // Per-connection recv ring (MPSC, batch snapshot)
    private readonly MpscRecvRing _recv = new(capacityPow2: 1024);
    
    

    // --- Reactor thread API -------------------------------------------------

    public long RingCount => _recv.GetTailHeadDiff();

    /// <summary>
    /// Called by reactor thread: enqueue recv buffer and wake if there is a waiter.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EnqueueRecv(byte* ptr, int length, ushort bufferId)
    {
        // If connection already closed/reused, just let reactor return the buffer elsewhere.
        if (Volatile.Read(ref _closed) != 0)
            return;

        // If ring is full, you must decide a policy: drop/close/backpressure.
        // Here: close semantics (safer than corrupting queue).
        if (!_recv.TryEnqueue(new RecvItem(ptr, length, bufferId)))
        {
            // Mark pending close (handler will observe and stop)
            Volatile.Write(ref _closed, 1);

            // Wake waiter so it can exit
            if (Interlocked.Exchange(ref _armed, 0) == 1)
                _readSignal.SetResult(ReadResult.Closed(error: 0));
            else
                Volatile.Write(ref _pending, 1);

            return;
        }

        // Wake edge-triggered
        if (Interlocked.Exchange(ref _armed, 0) == 1)
        {
            // Provide a tail snapshot for this batch boundary
            long snap = _recv.SnapshotTail();
            _readSignal.SetResult(new ReadResult(snap, isClosed: false));
        }
        else
        {
            Volatile.Write(ref _pending, 1);
        }
    }

    /// <summary>
    /// Called by reactor thread when fd is closing (res <= 0).
    /// Ensures awaiting handler wakes up and future reads return closed.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void MarkClosed(int error = 0)
    {
        Volatile.Write(ref _closed, 1);

        if (Interlocked.Exchange(ref _armed, 0) == 1)
            _readSignal.SetResult(ReadResult.Closed(error));
        else
            Volatile.Write(ref _pending, 1);
    }

    // --- Handler thread API -------------------------------------------------

    /// <summary>
    /// Await until there is at least one recv available OR the connection is closed.
    /// Returns a tail snapshot that defines the batch boundary.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<ReadResult> ReadAsync() 
    {
        // If already closed (or reused), complete synchronously as closed.
        if (Volatile.Read(ref _closed) != 0)
            return new ValueTask<ReadResult>(ReadResult.Closed());

        // Fast path: something pending or ring not empty
        if (Volatile.Read(ref _pending) == 1 || !_recv.IsEmpty())
        {
            Volatile.Write(ref _pending, 0);

            // It might have become closed just now
            if (Volatile.Read(ref _closed) != 0)
                return new ValueTask<ReadResult>(ReadResult.Closed());

            long snap = _recv.SnapshotTail();
            return new ValueTask<ReadResult>(new ReadResult(snap, isClosed: false));
        }

        // Only one waiter is allowed
        if (Interlocked.Exchange(ref _armed, 1) == 1)
            throw new InvalidOperationException("ReadAsync already armed.");
 
        // Capture generation to guard pooled reuse
        int gen = Volatile.Read(ref _generation);

        // If it closed between checks and arm, avoid hanging
        if (Volatile.Read(ref _closed) != 0)
        {
            Interlocked.Exchange(ref _armed, 0);
            return new ValueTask<ReadResult>(ReadResult.Closed());
        }

        return new ValueTask<ReadResult>(this, (short)gen);
        //return new ValueTask<ReadResult>(this, _readSignal.Version);
    }

    /// <summary>
    /// Drain one batch (bounded by the tail snapshot you got from ReadAsync).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetRing(long tailSnapshot, out RecvItem item)
        => _recv.TryDequeueUntil(tailSnapshot, out item);

    /// <summary>
    /// Prepare for next wait cycle (call after finishing draining the batch).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ResetRead()
    {
        _readSignal.Reset();

        // If data arrived while we were processing, make next ReadAsync fast-path.
        if (!_recv.IsEmpty())
            Volatile.Write(ref _pending, 1);

        // If it closed while we were processing, ensure next ReadAsync returns closed immediately.
        if (Volatile.Read(ref _closed) != 0)
            Volatile.Write(ref _pending, 1);
    }

    // --- Pooling / lifecycle ------------------------------------------------

    public void Clear()
    {
        // Invalidate any awaiting token by bumping generation
        Interlocked.Increment(ref _generation);

        // Mark closed so any late handler calls don't wait
        Volatile.Write(ref _closed, 1);

        ResetWriteBuffer();
        CanWrite = false;
        CanFlush = true;

        // Reset read state
        Volatile.Write(ref _armed, 0);
        Volatile.Write(ref _pending, 0);
        _readSignal.Reset();
        _recv.Clear();
    }

    public Connection SetFd(int fd) { ClientFd = fd; return this; }

    public Connection SetReactor(Engine.Engine.Reactor reactor)
    {
        Reactor = reactor;

        // New live connection: open it
        Volatile.Write(ref _closed, 0);
        Volatile.Write(ref _pending, 0);
        Volatile.Write(ref _armed, 0);
        _readSignal.Reset();
        _recv.Clear();

        return this;
    }

    // --- IValueTaskSource<ReadResult> plumbing ------------------------------

    ReadResult IValueTaskSource<ReadResult>.GetResult(short token)
    {
        // token == generation at time of ReadAsync arm
        // If the connection was cleared/reused, treat as closed.
        if (token != (short)Volatile.Read(ref _generation))
            return ReadResult.Closed();

        return _readSignal.GetResult(_readSignal.Version);
        //return _readSignal.GetResult(token);
    }

    ValueTaskSourceStatus IValueTaskSource<ReadResult>.GetStatus(short token)
    {
        if (token != (short)Volatile.Read(ref _generation))
            return ValueTaskSourceStatus.Succeeded; // will yield Closed() in GetResult

        return _readSignal.GetStatus(_readSignal.Version);
    }

    void IValueTaskSource<ReadResult>.OnCompleted(
        Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
    {
        if (token != (short)Volatile.Read(ref _generation))
        {
            // Complete immediately if it was reused
            continuation(state);
            return;
        }

        _readSignal.OnCompleted(continuation, state, _readSignal.Version, flags);
    }
    
    public UnmanagedMemoryManager[] GetAllRings(ReadResult readResult) 
    {
        var count = RingCount;

        if (count == 1) 
        {
            TryGetRing(readResult.TailSnapshot, out var item);
            return [item.AsUnmanagedMemoryManager()];
        }
        
        var mems = new UnmanagedMemoryManager[count];
        for (int i = 0; i < count; i++) 
        {
            TryGetRing(readResult.TailSnapshot, out var item);
            mems[i] = item.AsUnmanagedMemoryManager();
        }
        
        return mems;
    }
}