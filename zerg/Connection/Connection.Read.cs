using System.Runtime.CompilerServices;
using System.Threading.Tasks.Sources;
using zerg.Utils;
using zerg.Utils.SingleProducerSingleConsumer;

namespace zerg;

/// <summary>
/// A pooled, unsafe connection object used by a reactor-style networking engine.
///
/// Concurrency model:
/// - Reactor thread produces inbound buffers by calling <see cref="EnqueueRingItem"/> and <see cref="MarkClosed"/>.
/// - A single handler/consumer awaits inbound data via <see cref="ReadAsync"/> and drains using
///   <see cref="TryGetRing"/> bounded by a tail snapshot.
/// - The connection can be reused (pooled). <see cref="_generation"/> guards ValueTask tokens against reuse.
///
/// Key invariants:
/// - Only ONE outstanding ReadAsync waiter is allowed per connection (enforced by <see cref="_armed"/>).
/// - The recv ring is MPSC; consumer drains in batches defined by a tail snapshot.
/// - A close transitions <see cref="_closed"/> from 0 to 1 (published), waking any waiter.
/// </summary>
[SkipLocalsInit]
public sealed partial class Connection
{
    // =========================================================================
    // Read completion & state (single-consumer)
    // =========================================================================

    /// <summary>
    /// Completion primitive for the single awaiting ReadAsync().
    /// NOTE: ValueTask tokening is guarded by _generation, not by _readSignal.Version.
    /// </summary>
    private ManualResetValueTaskSourceCore<ReadResult> _readSignal;

    /// <summary>
    /// 1 when a handler is waiting (armed) and must be woken by producer; otherwise 0.
    /// Enforces: only one waiter at a time.
    /// </summary>
    private int _armed;

    /// <summary>
    /// 1 indicates data (or close) arrived while not armed; makes next ReadAsync fast-path.
    /// (edge-trigger style)
    /// </summary>
    private int _pending;

    // =========================================================================
    // Lifetime / pooling safety
    // =========================================================================

    /// <summary>
    /// Published close flag.
    /// 0 = open, 1 = closed (or connection is being/has been reused).
    /// </summary>
    private int _closed;

    /// <summary>
    /// Incremented on Clear()/reuse. Used as ValueTask token to invalidate old awaiters.
    /// </summary>
    private int _generation;

    // =========================================================================
    // Inbound recv ring (MPSC)
    // =========================================================================

    /// <summary>
    /// Per-connection inbound ring.
    /// Producers (reactor threads) enqueue received buffers; consumer drains.
    /// </summary>
    private readonly SpscRecvRing _recv = new(capacityPow2: 1024);

    // =========================================================================
    // Reactor thread API (producer side)
    // =========================================================================

    /// <summary>
    /// Mark the connection as closed and wake any awaiting handler.
    ///
    /// Called by reactor thread when the underlying fd is closing (recv returns 0 or error).
    /// After this is published, all future reads should complete as closed.
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

    // =========================================================================
    // Handler thread API (consumer side)
    // =========================================================================

    /// <summary>
    /// Wait until there is at least one recv available OR the connection is closed.
    ///
    /// Returns:
    /// - A <see cref="ReadResult"/> with a tail snapshot that defines the batch boundary.
    ///   The consumer must drain items using that snapshot via <see cref="TryGetRing"/>.
    ///
    /// Rules:
    /// - Only one outstanding call may be awaiting at once (single waiter).
    /// - After draining the batch, call <see cref="ResetRead"/> to prepare for the next wait cycle.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<ReadResult> ReadAsync()
    {
        // If already closed (or reused), complete synchronously as closed.
        if (Volatile.Read(ref _closed) != 0)
            return new ValueTask<ReadResult>(ReadResult.Closed());

        // Fast path: pending signal or ring not empty.
        // Pending is used as an "edge" bit: producer sets it when it couldn't wake a waiter.
        if (Volatile.Read(ref _pending) == 1 || !_recv.IsEmpty())
        {
            Volatile.Write(ref _pending, 0);

            // It might have become closed just now.
            if (Volatile.Read(ref _closed) != 0)
                return new ValueTask<ReadResult>(ReadResult.Closed());

            long snap = _recv.SnapshotTail();
            SnapshotRingCount = (int)(snap - _recv.Head);
            
            return new ValueTask<ReadResult>(new ReadResult(snap, isClosed: false));
        }

        // Only one waiter is allowed.
        if (Interlocked.Exchange(ref _armed, 1) == 1)
            throw new InvalidOperationException("ReadAsync already armed.");

        // Capture generation to guard pooled reuse.
        int gen = Volatile.Read(ref _generation);

        // If it closed between checks and arm, avoid hanging.
        if (Volatile.Read(ref _closed) != 0)
        {
            Interlocked.Exchange(ref _armed, 0);
            return new ValueTask<ReadResult>(ReadResult.Closed());
        }

        // NOTE: token uses generation. The underlying completion still uses _readSignal.Version internally.
        return new ValueTask<ReadResult>(this, (short)gen);
    }

    /// <summary>
    /// Prepare for the next read cycle after you finish draining a batch.
    ///
    /// Why it exists:
    /// - ManualResetValueTaskSourceCore is single-use until Reset() is called.
    /// - Also re-establishes fast-path behavior when new data arrives while you were processing.
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

    // =========================================================================
    // IValueTaskSource<ReadResult> plumbing (tokened by generation)
    // =========================================================================

    /// <summary>
    /// Complete the awaiting ReadAsync().
    ///
    /// Token semantics:
    /// - token == generation captured at arm time.
    /// - If the connection was cleared/reused, treat as closed (do not leak data across lifetimes).
    /// </summary>
    ReadResult IValueTaskSource<ReadResult>.GetResult(short token)
    {
        if (token != (short)Volatile.Read(ref _generation))
            return ReadResult.Closed();

        // We use _readSignal.Version as the internal version because Reset() advances it.
        return _readSignal.GetResult(_readSignal.Version);
    }

    /// <summary>
    /// Report status for the awaiting ReadAsync().
    /// If token is stale (reused), we report Succeeded so the awaiter will call GetResult and observe Closed().
    /// </summary>
    ValueTaskSourceStatus IValueTaskSource<ReadResult>.GetStatus(short token)
    {
        if (token != (short)Volatile.Read(ref _generation))
            return ValueTaskSourceStatus.Succeeded;

        return _readSignal.GetStatus(_readSignal.Version);
    }

    /// <summary>
    /// Register continuation for the awaiting ReadAsync().
    /// If token is stale (reused), invoke continuation immediately.
    /// </summary>
    void IValueTaskSource<ReadResult>.OnCompleted(
        Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
    {
        if (token != (short)Volatile.Read(ref _generation))
        {
            continuation(state);
            return;
        }

        _readSignal.OnCompleted(continuation, state, _readSignal.Version, flags);
    }
}