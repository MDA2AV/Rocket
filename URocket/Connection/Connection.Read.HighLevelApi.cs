using System.Buffers;
using URocket.Utils;
using URocket.Utils.UnmanagedMemoryManager;

namespace URocket.Connection;

/// <summary>
/// <br/>
/// Represents a single reactor-owned connection with a snapshot-based
/// multi-segment receive ring.
/// <br/>
/// </summary>
/// <remarks>
/// <br/>
/// **High level API remarks**: The receiving side is modeled as a monotonic ring with snapshot semantics:
/// each <see cref="ReadResult"/> captures a logical tail position that acts as
/// a fence for how much data is visible in the current batch.
///
/// All “snapshot” APIs in this type operate relative to that fence and either
/// <b>peek</b> (non-destructive) or <b>dequeue</b> (destructive) the segments
/// that were present at the time of the snapshot.
///
/// Some helpers allocate and materialize arrays or lists for convenience.
/// Hot-path code should prefer streaming or direct ring access to avoid
/// allocations.
/// <br/>
/// </remarks>
public sealed partial class Connection 
{
    /// <summary>
    /// Current number of items in the ring (tail-head). Useful for metrics/debugging.
    /// </summary>
    public long TotalRingCount => _recv.GetTailHeadDiff();

    /// <summary>
    /// Snapshot number of items in the ring (tail-head). Calculated when tail snapshot is taken.
    /// </summary>
    public int SnapshotRingCount { get; private set; }
    
    // =========================================================================
    // Convenience batch API (allocation-y by design)
    // =========================================================================

    /// <summary>
    /// Builds a zero-copy <see cref="ReadOnlySequence{T}"/> over all ring segments
    /// that existed at the time of the given <see cref="ReadResult"/> snapshot.
    /// </summary>
    /// <remarks>
    /// Dequeues all receive-ring segments with a position less than
    /// <see cref="ReadResult.TailSnapshot"/> and links them into a segmented sequence.
    /// The underlying buffers are not copied; <paramref name="rings"/> is returned so the
    /// caller can keep them alive and later return/release them. This call permanently
    /// advances the ring head.
    /// </remarks>
    /// <param name="readResult">Snapshot describing how far to read.</param>
    /// <param name="rings">
    /// The dequeued ring buffers backing <paramref name="sequence"/> (ownership/lifetime handles),
    /// or <c>null</c> if no data was available.
    /// </param>
    /// <param name="sequence">The resulting sequence, or <c>default</c> if no data.</param>
    /// <returns><c>true</c> if data was dequeued; otherwise <c>false</c>.</returns>
    public bool TryDynamicallyGetAllSnapshotRingsAsReadOnlySequence(ReadResult readResult, out List<UnmanagedMemoryManager> rings, out ReadOnlySequence<byte> sequence)
    {
        rings = null!;
        var tailSnapshot = readResult.TailSnapshot;

        // First segment
        if (!_recv.TryDequeueUntil(tailSnapshot, out var headItem))
        {
            sequence = default;
            return false;
        }
        
        var innerRings = new List<UnmanagedMemoryManager>(2);
        rings = innerRings;

        var headMem = headItem.AsUnmanagedMemoryManager();
        var head = new RingSegment(headMem.Memory, headMem.BufferId);
        innerRings.Add(headMem);
        var tail = head;

        // Remaining segments up to snapshot
        while (_recv.TryDequeueUntil(tailSnapshot, out var item))
        {
            var mem = item.AsUnmanagedMemoryManager();
            tail = tail.Append(mem.Memory, mem.BufferId);
            innerRings.Add(mem);
        }

        sequence = new ReadOnlySequence<byte>(head, 0, tail, tail.Memory.Length);
        return true;
    }
    
    /// <summary>
    /// Dequeues all receive-ring segments that existed at the time of the given
    /// <see cref="ReadResult"/> snapshot and returns them as unmanaged memory owners.
    /// </summary>
    /// <remarks>
    /// All ring segments with a position less than <see cref="ReadResult.TailSnapshot"/>
    /// are removed from the receiving ring and returned as
    /// <see cref="UnmanagedMemoryManager"/> instances. No data is copied.
    /// The caller owns the returned buffers and is responsible for releasing or
    /// returning them after use. This call permanently advances the ring head.
    /// </remarks>
    /// <param name="readResult">Snapshot describing how far to read.</param>
    /// <param name="rings">
    /// The dequeued ring buffers, or <c>null</c> if no data was available.
    /// </param>
    /// <returns><c>true</c> if any data was dequeued; otherwise <c>false</c>.</returns>
    public bool TryDynamicallyGetAllSnapshotRingsAsUnmanagedMemory(ReadResult readResult, out List<UnmanagedMemoryManager> rings)
    {
        rings = null!;
        var tailSnapshot = readResult.TailSnapshot;

        // First segment
        if (!_recv.TryDequeueUntil(tailSnapshot, out var headItem))
        {
            return false;
        }
        
        var innerRings = new List<UnmanagedMemoryManager>(2);
        rings = innerRings;
        
        innerRings.Add(headItem.AsUnmanagedMemoryManager());
        
        // Remaining segments up to snapshot
        while (_recv.TryDequeueUntil(tailSnapshot, out var item))
        {
            innerRings.Add(item.AsUnmanagedMemoryManager());
        }
        
        return true;
    }
    
    /// <summary>
    /// Dequeues all receive-ring items that existed at the time of the given
    /// <see cref="ReadResult"/> snapshot and returns them as raw <see cref="RingItem"/>s.
    /// </summary>
    /// <remarks>
    /// All ring items with a position less than <see cref="ReadResult.TailSnapshot"/>
    /// are removed from the receiving ring and returned. No data is copied.
    /// The caller becomes the owner of the dequeued items and is responsible for
    /// processing and releasing them. This call permanently advances the ring head.
    /// </remarks>
    /// <param name="readResult">Snapshot describing how far to read.</param>
    /// <param name="rings">
    /// The dequeued ring items, or <c>null</c> if no data was available.
    /// </param>
    /// <returns><c>true</c> if any data was dequeued; otherwise <c>false</c>.</returns>
    public bool TryDynamicallyGetAllSnapshotRings(ReadResult readResult, out List<RingItem> rings)
    {
        rings = null!;
        var tailSnapshot = readResult.TailSnapshot;

        // First segment
        if (!_recv.TryDequeueUntil(tailSnapshot, out var headItem))
        {
            return false;
        }
        
        var innerRings = new List<RingItem>(2);
        rings = innerRings;
        
        innerRings.Add(headItem);
        
        // Remaining segments up to snapshot
        while (_recv.TryDequeueUntil(tailSnapshot, out var item))
        {
            innerRings.Add(item);
        }
        
        return true;
    }
    
    /// <summary>
    /// Dequeues all ring items visible in the current snapshot batch and converts
    /// them to <see cref="UnmanagedMemoryManager"/> instances.
    /// </summary>
    /// <remarks>
    /// This method uses <see cref="SnapshotRingCount"/> to determine how many items
    /// belong to the batch and removes them from the receiving ring using the
    /// snapshot fence. The returned buffers are not copied; the caller owns them
    /// and must release or return them after use.
    /// </remarks>
    public UnmanagedMemoryManager[] GetAllSnapshotRingsAsUnmanagedMemory(ReadResult readResult)
    {
        var count = SnapshotRingCount;

        if (count == 1)
            return [GetRing().AsUnmanagedMemoryManager()];
        
        var mems = new UnmanagedMemoryManager[count];
        for (int i = 0; i < count; i++)
            mems[i] = GetRing().AsUnmanagedMemoryManager();

        return mems;
    }
    
    /// <summary>
    /// Dequeues all ring items visible in the current snapshot batch.
    /// </summary>
    /// <remarks>
    /// This method removes exactly <see cref="SnapshotRingCount"/> items from the
    /// receiving ring using the snapshot fence and returns them as raw
    /// <see cref="RingItem"/> values. Ownership is transferred to the caller.
    /// </remarks>
    public RingItem[] GetAllSnapshotRings(ReadResult readResult)
    {
        var count = SnapshotRingCount;

        if (count == 1)
            return [GetRing()];
        
        var items = new RingItem[count];
        for (int i = 0; i < count; i++)
            items[i] = GetRing();

        return items;
    }
}