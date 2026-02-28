using System.Runtime.CompilerServices;
using zerg.Utils;

namespace zerg;

public sealed unsafe partial class Connection 
{
    /// <summary>
    /// Drain one item from the ring, but only up to the batch boundary defined by <paramref name="tailSnapshot"/>.
    /// Returns false once the consumer has drained the current batch.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetRing(long tailSnapshot, out RingItem item)
        => _recv.TryDequeueUntil(tailSnapshot, out item);

    /// <summary>
    /// Drain one item from the ring.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RingItem GetRing() => _recv.DequeueSingle();
    
    /// <summary>
    /// Enqueue a received buffer into the inbound ring and wake a waiter if present.
    ///
    /// Producer contract:
    /// - Called by reactor thread(s) when a recv completes for this connection.
    /// - If the connection is already closed/reused, this method is a no-op (caller should
    ///   return buffer elsewhere).
    ///
    /// Wakeup behavior:
    /// - If a handler is currently armed, we atomically disarm and complete the ValueTask
    ///   with a batch boundary (<see cref="RingSnapshot.TailSnapshot"/>).
    /// - If no handler is armed, we set <see cref="_pending"/> so the next ReadAsync fast-paths.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EnqueueRingItem(byte* ptr, int length, ushort bufferId)
    {
        // If connection already closed/reused, just let reactor return the buffer elsewhere.
        if (Volatile.Read(ref _closed) != 0)
            return;

        // Ring full policy: close the connection (safer than corrupting the queue).
        // Alternative policies: drop, backpressure, expand ring.
        if (!_recv.TryEnqueue(new RingItem(ptr, length, bufferId)))
        {
            // Publish close.
            Volatile.Write(ref _closed, 1);

            // Wake waiter (if any) so it can exit.
            if (Interlocked.Exchange(ref _armed, 0) == 1)
                _readSignal.SetResult(RingSnapshot.Closed(error: 0));
            else
                Volatile.Write(ref _pending, 1);

            return;
        }

        // Edge-trigger wake:
        // If there is an armed waiter, complete it with a tail snapshot.
        if (Interlocked.Exchange(ref _armed, 0) == 1)
        {
            long snap = _recv.SnapshotTail();
            SnapshotRingCount = (int)(snap - _recv.Head);
            _readSignal.SetResult(new RingSnapshot(snap, isClosed: false));
        }
        else
        {
            // No waiter: mark pending so the next ReadAsync does not park.
            Volatile.Write(ref _pending, 1);
        }
    }
    
    // =========================================================================
    // Buffer return (back to reactor-owned pool)
    // =========================================================================

    /// <summary>
    /// Return a previously received buffer back to the reactor's buffer pool via its return queue.
    /// Typically called by the consumer after it is done processing a RingItem.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReturnRing(ushort bufferId)
    {
        if (IncrementalMode)
            Reactor.EnqueueReturnQIncremental(ClientFd, bufferId);
        else
            Reactor.EnqueueReturnQ(bufferId);
    }
}