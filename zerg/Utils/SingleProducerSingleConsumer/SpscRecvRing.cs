using System.Runtime.CompilerServices;

namespace zerg.Utils.SingleProducerSingleConsumer;

public sealed class SpscRecvRing
{
    private readonly RingItem[] _items;
    private readonly int _mask;

    // SPSC:
    // _tail written only by producer, read by consumer
    // _head written only by consumer, read by producer
    private long _tail;
    private long _head;

    public long Head => Volatile.Read(ref _head);

    public SpscRecvRing(int capacityPow2)
    {
        if (capacityPow2 <= 0 || (capacityPow2 & (capacityPow2 - 1)) != 0)
            throw new ArgumentException("capacityPow2 must be a power of two", nameof(capacityPow2));

        _items = new RingItem[capacityPow2];
        _mask  = capacityPow2 - 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryEnqueue(in RingItem item)
    {
        long head = Volatile.Read(ref _head); // observe consumer progress
        long tail = _tail;                    // producer-local

        // full?
        if ((ulong)(tail - head) >= (ulong)_items.Length)
            return false;

        _items[(int)(tail & _mask)] = item;   // store payload first
        Volatile.Write(ref _tail, tail + 1);  // publish tail (release)
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long SnapshotTail() => Volatile.Read(ref _tail); // acquire

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryDequeueUntil(long tailSnapshot, out RingItem item)
    {
        long head = _head; // consumer-local
        if (head >= tailSnapshot)
        {
            item = default;
            return false;
        }

        item = _items[(int)(head & _mask)];
        Volatile.Write(ref _head, head + 1);
        return true;
    }

    // Consumer-only: assumes NOT empty (or you already checked with a snapshot / IsEmpty)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RingItem DequeueSingle()
    {
        long head = _head;
        RingItem item = _items[(int)(head & _mask)];
        Volatile.Write(ref _head, head + 1);
        return item;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsEmpty()
        => Volatile.Read(ref _head) >= Volatile.Read(ref _tail);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long GetTailHeadDiff()
        => Volatile.Read(ref _tail) - Volatile.Read(ref _head);

    public void Clear()
    {
        Volatile.Write(ref _head, 0);
        Volatile.Write(ref _tail, 0);
    }
}