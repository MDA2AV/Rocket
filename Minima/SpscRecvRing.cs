using System.Runtime.CompilerServices;

namespace Minima;

/// <summary>
/// Bounded single-producer / single-consumer ring of recv items.
/// Producer is the reactor's CQE dispatcher; consumer is the connection's
/// async handler. _tail is written only by the producer, _head only by the
/// consumer — Volatile fences pair the publish/acquire so the data is safe
/// even when the consumer runs on a different thread (e.g. when the user
/// flips RunContinuationsAsynchronously to true).
/// </summary>
internal sealed class SpscRecvRing
{
    public struct Item
    {
        public ushort Bid;
        public int Len;
        public bool HasBuffer;
    }

    private readonly Item[] _items;
    private readonly int _mask;

    private long _tail;   // producer writes, consumer reads
    private long _head;   // consumer writes, producer reads

    public SpscRecvRing(int capacityPow2)
    {
        if (capacityPow2 <= 0 || (capacityPow2 & (capacityPow2 - 1)) != 0)
            throw new ArgumentException("capacity must be a power of two", nameof(capacityPow2));

        _items = new Item[capacityPow2];
        _mask  = capacityPow2 - 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryEnqueue(in Item item)
    {
        long head = Volatile.Read(ref _head);    // observe consumer progress
        long tail = _tail;                       // producer-local
        if ((ulong)(tail - head) >= (ulong)_items.Length)
            return false;

        _items[(int)(tail & _mask)] = item;      // store payload first
        Volatile.Write(ref _tail, tail + 1);     // publish (release)
        return true;
    }

    /// <summary>
    /// Capture the producer's current tail position. Items from the consumer's
    /// head up to (but not including) this snapshot are guaranteed available.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long SnapshotTail() => Volatile.Read(ref _tail);

    /// <summary>
    /// Dequeue one item if head is below the given snapshot.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryDequeueUntil(long tailSnapshot, out Item item)
    {
        long head = _head;                       // consumer-local
        if (head >= tailSnapshot)
        {
            item = default;
            return false;
        }

        item = _items[(int)(head & _mask)];
        Volatile.Write(ref _head, head + 1);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsEmpty()
        => Volatile.Read(ref _head) >= Volatile.Read(ref _tail);

    public void Clear()
    {
        Volatile.Write(ref _head, 0);
        Volatile.Write(ref _tail, 0);
    }
}
