using System.Runtime.CompilerServices;

namespace URocket.Utils.SingleProducerSingleConsumer;

public sealed class SpscRecvRing
{
    private readonly RingItem[] _items;
    private readonly int _mask;

    // single-writer each:
    // _tail written only by producer
    // _head written only by consumer
    private int _tail;
    private int _head;

    public SpscRecvRing(int capacityPow2)
    {
        if (capacityPow2 <= 0 || (capacityPow2 & (capacityPow2 - 1)) != 0)
            throw new ArgumentException("capacityPow2 must be a power of two", nameof(capacityPow2));

        _items = new RingItem[capacityPow2];
        _mask = capacityPow2 - 1;
    }

    // Producer thread only
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryEnqueue(in RingItem item)
    {
        int head = Volatile.Read(ref _head); // observe consumer progress
        int tail = _tail;                    // producer-local
        if (tail - head >= _items.Length)
            return false; // full

        _items[tail & _mask] = item;                 // store payload first
        Volatile.Write(ref _tail, tail + 1);         // publish (release)
        return true;
    }

    // Consumer thread only
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int SnapshotTail() => Volatile.Read(ref _tail); // acquire

    // Consumer thread only
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryDequeueUntil(int tailSnapshot, out RingItem item)
    {
        int head = _head; // consumer-local
        if (head >= tailSnapshot)
        {
            item = default;
            return false;
        }

        item = _items[head & _mask];
        Volatile.Write(ref _head, head + 1); // publish consumer progress
        return true;
    }

    // Consumer thread only
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsEmpty()
        => Volatile.Read(ref _head) >= Volatile.Read(ref _tail);

    public void Clear()
    {
        Volatile.Write(ref _head, 0);
        Volatile.Write(ref _tail, 0);
    }

    public int Count => Volatile.Read(ref _tail) - Volatile.Read(ref _head);
}
