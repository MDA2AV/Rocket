using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Tasks.Sources;
using URocket.Utils;

namespace URocket;

public readonly unsafe struct RecvItem {
    public readonly byte* Ptr;
    public readonly int Length;
    public readonly ushort BufferId;

    public RecvItem(byte* ptr, int length, ushort bufferId) {
        Ptr = ptr;
        Length = length;
        BufferId = bufferId;
    }
}

[SkipLocalsInit]
public sealed unsafe class Connection : IValueTaskSource<ReadResult>
{
    public int ClientFd { get; private set; }
    public Engine.Engine.Reactor Reactor { get; private set; } = null!;

    // Out buffer
    public nuint OutHead { get; set; }
    public nuint OutTail { get; set; }
    public byte* OutPtr  { get; set; }

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
            int snap = _recv.SnapshotTail();
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

            int snap = _recv.SnapshotTail();
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
    }

    /// <summary>
    /// Drain one batch (bounded by the tail snapshot you got from ReadAsync).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryDequeueBatch(int tailSnapshot, out RecvItem item)
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

        // Reset send state
        OutPtr = null;
        OutHead = 0;
        OutTail = 0;

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
}

/*
[SkipLocalsInit]
public sealed unsafe class Connection : IValueTaskSource<ReadResult>
{
    public int ClientFd { get; private set; }
    public Engine.Engine.Reactor Reactor { get; private set; } = null!;

    // Out buffer
    public nuint OutHead { get; set; }
    public nuint OutTail { get; set; }
    public byte* OutPtr { get; set; }

    // Reusable completion primitive
    private ManualResetValueTaskSourceCore<ReadResult> _readSignal;

    // 0/1 flags (atomic)
    private int _armed;    // there is an outstanding await that needs waking
    private int _pending;  // data arrived while not armed / before reset

    // Queue of received buffers for this connection
    private readonly ConcurrentQueue<RecvItem> _recvQ = new();

    private ReadResult BuildResult() {
        return new ReadResult(BuildSequence(), false, false);
    }
    
    internal List<ushort> BufferIds { get; } = new List<ushort>();

    public void ReturnBuffers() {
        foreach (var bufferId in BufferIds)
            Reactor.EnqueueReturnQ(bufferId);
    }
    
    private ReadOnlySequence<byte> BuildSequence() {
        BufferIds.Clear();
        
        if (!_recvQ.TryDequeue(out var item))
            return ReadOnlySequence<byte>.Empty;

        // first segment
        var mm = new UnmanagedMemoryManager(item.Ptr, item.Length);
        var firstSeg = new BufferSegment(mm.Memory);
        var lastSeg  = firstSeg;
        
        BufferIds.Add(item.BufferId);

        // remaining segments
        while (_recvQ.TryDequeue(out item)) {
            BufferIds.Add(item.BufferId);
            mm = new UnmanagedMemoryManager(item.Ptr, item.Length);
            lastSeg = lastSeg.Append(mm.Memory);
        }

        return new ReadOnlySequence<byte>(
            firstSeg,
            0,
            lastSeg,
            lastSeg.Memory.Length);
    }
    
    /// <summary>
    /// [Called by reactor thread: push recv and wake if needed.]
    /// Enqueue from the reactor thread, if there is already someone waiting (armed):
    ///     Signal it
    /// else:
    ///     Set pending, meaning that when some client calls ReadAsync, gets immediately served
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EnqueueRecv(byte* ptr, int length, ushort bufferId) {
        _recvQ.Enqueue(new RecvItem(ptr, length, bufferId));
        // Edge-trigger: if a waiter is armed, wake it; otherwise mark pending.
        if (Interlocked.Exchange(ref _armed, 0) == 1)  _readSignal.SetResult(BuildResult());
        else Volatile.Write(ref _pending, 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryDequeueRecv(out RecvItem item) => _recvQ.TryDequeue(out item);

    /// <summary>
    /// [Await until there is at least one recv item available.]
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<ReadResult> ReadAsync()
    {
        // Fast path: data already pending or already queued
        // “Is data already available?”
        if (Volatile.Read(ref _pending) == 1 || !_recvQ.IsEmpty) {
            Volatile.Write(ref _pending, 0);
            return new ValueTask<ReadResult>(BuildResult());
        }

        // Arm exactly one waiter
        if (Interlocked.Exchange(ref _armed, 1) == 1)
            throw new InvalidOperationException("ReadAsync already armed.");

        return new ValueTask<ReadResult>(this, _readSignal.Version);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ResetRead()
    {
        // Prepare the value-task source for the next wait cycle.
        _readSignal.Reset();

        // If data arrived between the time we finished draining and now,
        // ensure the next ReadAsync() completes synchronously.
        if (!_recvQ.IsEmpty)
            Volatile.Write(ref _pending, 1);
    }

    public void Clear()
    {
        BufferIds.Clear();
        
        OutPtr = null;
        OutHead = 0;
        OutTail = 0;

        Volatile.Write(ref _armed, 0);
        Volatile.Write(ref _pending, 0);
        _readSignal.Reset();

        while (_recvQ.TryDequeue(out _)) { }
    }

    public Connection SetFd(int fd) { ClientFd = fd; return this; }
    public Connection SetReactor(Engine.Engine.Reactor reactor) { Reactor = reactor; return this; }

    // IValueTaskSource<bool>
    ReadResult IValueTaskSource<ReadResult>.GetResult(short token) => _readSignal.GetResult(token);
    ValueTaskSourceStatus IValueTaskSource<ReadResult>.GetStatus(short token) => _readSignal.GetStatus(token);
    void IValueTaskSource<ReadResult>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        => _readSignal.OnCompleted(continuation, state, token, flags);
}
*/

/*
[SkipLocalsInit]
public sealed unsafe class Connection2 : IValueTaskSource<bool>
{
    public bool HasBuffer;
    public ushort BufferId { get; set; }

    public int ClientFd { get; private set; }
    public Engine.Engine.Reactor Reactor { get; private set; } = null!;

    // In buffer (points into reactor's buffer-ring slab)
    public byte* InPtr { get; internal set; }
    public int InLength  { get; internal set; }

    // Out buffer
    public nuint OutHead { get; set; }
    public nuint OutTail { get; set; }
    public byte* OutPtr { get; set; }

    // Reusable completion primitive (no Task/TCS allocations per read)
    private ManualResetValueTaskSourceCore<bool> _readSignal;

    // Debug guard: enforces "one outstanding ReadAsync at a time"
    private bool _readArmed;

    /// <summary>
    /// Await until the reactor signals that new bytes are available in InPtr/InLength.
    /// One outstanding await is supported at a time.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<bool> ReadAsync() {
        if (_readArmed) 
            throw new InvalidOperationException("ReadAsync() already armed. Call ResetRead() after consuming the buffer.");
        _readArmed = true;
        Console.WriteLine($"{Reactor.Id} _readArmed TRUE");
        return new ValueTask<bool>(this, _readSignal.Version);
    }

    /// <summary>
    /// Called by the reactor thread when it has produced readable bytes for this connection.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SignalReadReady() { _readSignal.SetResult(true); }

    /// <summary>
    /// Called by the consumer after it finishes using InPtr/InLength and wants to await the next read.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ResetRead() {
        _readArmed = false;

        // Prepare for next await cycle
        Console.WriteLine($"{Reactor.Id} Resetting..");
        _readSignal.Reset();
        Console.WriteLine($"{Reactor.Id} _readArmed FALSE");

        // Clear "current read" metadata (optional but helps avoid misuse)
        //InPtr = null;
        //InLength = 0;
        //HasBuffer = false;
        //BufferId = 0;
    }

    public void Clear() {
        // Reset send state
        OutPtr = null;
        OutHead = 0;
        OutTail = 0;

        // Reset read state so pooled connections don't remain signaled
        _readArmed = false;
        _readSignal.Reset();

        InPtr = null;
        InLength = 0;
        HasBuffer = false;
        BufferId = 0;
    }

    // Setters for pooled connections
    public Connection2 SetFd(int fd) { ClientFd = fd; return this; }
    public Connection2 SetReactor(Engine.Engine.Reactor reactor) { Reactor = reactor; return this; }
    
    // IValueTaskSource<bool> plumbing
    bool IValueTaskSource<bool>.GetResult(short token) => _readSignal.GetResult(token);
    ValueTaskSourceStatus IValueTaskSource<bool>.GetStatus(short token) => _readSignal.GetStatus(token);
    void IValueTaskSource<bool>.OnCompleted(
        Action<object?> continuation,
        object? state,
        short token,
        ValueTaskSourceOnCompletedFlags flags)
        => _readSignal.OnCompleted(continuation, state, token, flags);
}

[SkipLocalsInit]
public sealed unsafe class Connection3 : IValueTaskSource<bool>
{
    public int ClientFd { get; private set; }
    public Engine.Engine.Reactor Reactor { get; private set; } = null!;

    // Out buffer
    public nuint OutHead { get; set; }
    public nuint OutTail { get; set; }
    public byte* OutPtr { get; set; }

    // Reusable completion primitive
    private ManualResetValueTaskSourceCore<bool> _readSignal;

    // 0/1 flags (atomic)
    private int _armed;    // there is an outstanding await that needs waking
    private int _pending;  // data arrived while not armed / before reset

    // Queue of received buffers for this connection
    private readonly ConcurrentQueue<RecvItem> _recvQ = new();
    
    private ReadOnlySequence<byte> BuildSequence() {
        if (!_recvQ.TryDequeue(out var item))
            return ReadOnlySequence<byte>.Empty;

        // first segment
        var mm = new UnmanagedMemoryManager(item.Ptr, item.Length);
        var firstSeg = new BufferSegment(mm.Memory);
        var lastSeg  = firstSeg;

        // remaining segments
        while (_recvQ.TryDequeue(out item)) {
            mm = new UnmanagedMemoryManager(item.Ptr, item.Length);
            lastSeg = lastSeg.Append(mm.Memory);
        }

        return new ReadOnlySequence<byte>(
            firstSeg,
            0,
            lastSeg,
            lastSeg.Memory.Length);
    }
    
    /// <summary>
    /// [Called by reactor thread: push recv and wake if needed.]
    /// Enqueue from the reactor thread, if there is already someone waiting (armed):
    ///     Signal it
    /// else:
    ///     Set pending, meaning that when some client calls ReadAsync, gets immediately served
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EnqueueRecv(byte* ptr, int length, ushort bufferId) {
        _recvQ.Enqueue(new RecvItem(ptr, length, bufferId));
        // Edge-trigger: if a waiter is armed, wake it; otherwise mark pending.
        if (Interlocked.Exchange(ref _armed, 0) == 1)  _readSignal.SetResult(true);
        else Volatile.Write(ref _pending, 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryDequeueRecv(out RecvItem item) => _recvQ.TryDequeue(out item);

    /// <summary>
    /// [Await until there is at least one recv item available.]
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<bool> ReadAsync()
    {
        // Fast path: data already pending or already queued
        // “Is data already available?”
        if (Volatile.Read(ref _pending) == 1 || !_recvQ.IsEmpty) {
            Volatile.Write(ref _pending, 0);
            return new ValueTask<bool>(true);
        }

        // Arm exactly one waiter
        if (Interlocked.Exchange(ref _armed, 1) == 1)
            throw new InvalidOperationException("ReadAsync already armed.");

        return new ValueTask<bool>(this, _readSignal.Version);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ResetRead()
    {
        // Prepare the value-task source for the next wait cycle.
        _readSignal.Reset();

        // If data arrived between the time we finished draining and now,
        // ensure the next ReadAsync() completes synchronously.
        if (!_recvQ.IsEmpty)
            Volatile.Write(ref _pending, 1);
    }

    public void Clear()
    {
        OutPtr = null;
        OutHead = 0;
        OutTail = 0;

        Volatile.Write(ref _armed, 0);
        Volatile.Write(ref _pending, 0);
        _readSignal.Reset();

        while (_recvQ.TryDequeue(out _)) { }
    }

    public Connection3 SetFd(int fd) { ClientFd = fd; return this; }
    public Connection3 SetReactor(Engine.Engine.Reactor reactor) { Reactor = reactor; return this; }

    // IValueTaskSource<bool>
    bool IValueTaskSource<bool>.GetResult(short token) => _readSignal.GetResult(token);
    ValueTaskSourceStatus IValueTaskSource<bool>.GetStatus(short token) => _readSignal.GetStatus(token);
    void IValueTaskSource<bool>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        => _readSignal.OnCompleted(continuation, state, token, flags);
}
*/