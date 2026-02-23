using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks.Sources;
using zerg.Utils.UnmanagedMemoryManager;

namespace zerg;

/// <summary>
/// <br/>
/// <br/>
/// Connection-owned outbound write staging buffer + flush completion primitive.
///
/// Design / ownership model:
/// - The connection owns an unmanaged fixed-size slab (<see cref="WriteBuffer"/>) used for staging outgoing bytes.
/// - A *single producer* (typically the request handler / user code) appends data by advancing <see cref="WriteTail"/>.
/// - A *single consumer* (the reactor thread) flushes the staged bytes to the transport and completes the flush waiter.
///
/// Threading / correctness rules:
/// - Writes are allowed only when no flush is active (<see cref="_flushInProgress"/> == 0).
/// - <see cref="FlushAsync"/> arms a single waiter and publishes a snapshot target (<see cref="WriteInFlight"/>).
/// - The reactor must eventually call <see cref="CompleteFlush"/> exactly once per armed flush.
///
/// Invariants:
/// - Valid staged data range is [<see cref="WriteHead"/>, <see cref="WriteTail"/>).
/// - Flush target is a snapshot of tail captured in <see cref="FlushAsync"/> and stored in <see cref="WriteInFlight"/>.
/// - While flushing, producer must not modify any bytes below <see cref="WriteInFlight"/>.
/// <br/>
/// <br/>
/// </summary>
public unsafe partial class Connection
{
    /// <summary>
    /// Underlying completion source for <see cref="FlushAsync"/>. This enables allocation-free
    /// ValueTask completion with a single waiter per flush cycle.
    /// </summary>
    private ManualResetValueTaskSourceCore<bool> _flushSignal;
    
    /// <summary>
    /// Guard to ensure only one flush waiter is armed at a time.
    /// - 0: no waiter armed
    /// - 1: waiter armed (a ValueTask has been handed out)
    /// </summary>
    private int _flushArmed;
    
    /// <summary>
    /// Write/flush barrier.
    /// - 0: writes allowed
    /// - 1: flush in progress (producer must not write)
    /// </summary>
    private int _flushInProgress;
    
    /// <summary>
    /// True when a flush is active; used to enforce the "no writes while flushing" rule.
    /// </summary>
    internal bool IsFlushInProgress => Volatile.Read(ref _flushInProgress) != 0;
    
    /// <summary>Size of the unmanaged write slab.</summary>
    private readonly int _writeSlabSize;

    /// <summary>
    /// Memory owner for the unmanaged write slab. Provides a managed <see cref="Memory{T}"/> view
    /// over <see cref="WriteBuffer"/> for the IBufferWriter APIs.
    /// </summary>
    private readonly UnmanagedMemoryManager _manager;

    /// <summary>
    /// Pointer to the unmanaged write slab. Bytes are staged here before being flushed by the reactor.
    /// </summary>
    public byte* WriteBuffer { get; }

    /// <summary>
    /// Logical start of valid staged data. Currently unused (kept at 0), but reserved for streaming /
    /// partial flush scenarios.
    /// </summary>
    internal int WriteHead { get; set; }

    /// <summary>
    /// Logical end of written staged data in <see cref="WriteBuffer"/>. Producer appends here.
    /// </summary>
    public int WriteTail { get; private set; }

    /// <summary>
    /// Logical end of written staged data in <see cref="WriteBuffer"/>. Producer appends here.
    /// </summary>
    internal int WriteInFlight { get; set; }
    
    /// <summary>
    /// Reactor-owned flag:
    /// - 0: no send SQE currently outstanding for this connection
    /// - 1: send SQE outstanding (data is in-flight in kernel)
    /// </summary>
    internal int SendInflight; // 0/1

    /// <summary>
    /// Creates a new unmanaged write slab for the connection.
    /// </summary>
    /// <param name="writeSlabSize">Size in bytes of the slab.</param>
    public Connection(int writeSlabSize = 1024 * 16) {
        _writeSlabSize = writeSlabSize;
        WriteBuffer = (byte*)NativeMemory.AlignedAlloc((nuint)(writeSlabSize), 64);
        WriteHead = 0; WriteTail = 0;
        
        _manager = new UnmanagedMemoryManager(WriteBuffer, writeSlabSize);
    }
    
    /// <summary>
    /// Resets the logical range markers so the slab can be reused for the next batch.
    /// Does not clear the underlying memory (bytes are considered overwritten on next writes).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ResetWriteBuffer()
    {
        WriteHead = 0;
        WriteTail = 0; 
    }
    

    /// <summary>
    /// Completes the active flush waiter (reactor thread).
    /// Must be called exactly once for each successful <see cref="FlushAsync"/> that armed a waiter.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void CompleteFlush()
    {
        // Allow next write/flush cycle BEFORE signaling completion.
        // SetResult can resume the handler synchronously (inline continuation),
        // so barriers must be cleared first or the handler will see _flushInProgress == 1.
        Volatile.Write(ref _flushInProgress, 0);
        Volatile.Write(ref _flushArmed, 0);

        // Resume waiter (may execute handler inline on this thread)
        _flushSignal.SetResult(true);
    }

    // ----------------------
    // IValueTaskSource plumbing
    // ----------------------
    // Single waiter, token unused. Version is used to validate the current cycle.
    
    void IValueTaskSource.GetResult(short token)
        => _flushSignal.GetResult(_flushSignal.Version);

    ValueTaskSourceStatus IValueTaskSource.GetStatus(short token)
        => _flushSignal.GetStatus(_flushSignal.Version);

    void IValueTaskSource.OnCompleted(Action<object?> continuation, object? state, short token,
        ValueTaskSourceOnCompletedFlags flags)
        => _flushSignal.OnCompleted(continuation, state, _flushSignal.Version, flags);
}