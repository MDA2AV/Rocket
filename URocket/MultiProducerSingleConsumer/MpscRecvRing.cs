using System.Runtime.CompilerServices;

namespace URocket.MultiProducerSingleConsumer;

public sealed unsafe class MpscRecvRing
{
    private readonly RecvItem[] _items;
    private readonly int _mask;

    // TODO: Should be long
    private int _tail; // producer-reserved count
    private int _head; // consumer position

    public MpscRecvRing(int capacityPow2) {
        if (capacityPow2 <= 0 || (capacityPow2 & (capacityPow2 - 1)) != 0)
            throw new ArgumentException("capacityPow2 must be a power of two", nameof(capacityPow2));

        _items = new RecvItem[capacityPow2];
        _mask  = capacityPow2 - 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryEnqueue(in RecvItem item) {
        // Fast full check (approx) using current head/tail
        int head = Volatile.Read(ref _head);
        int tail = Volatile.Read(ref _tail);
        if (tail - head >= _items.Length) return false; // full

        // Reserve a unique slot
        int slot = Interlocked.Increment(ref _tail) - 1;

        // Store item
        _items[slot & _mask] = item;

        // Interlocked.Increment is a full fence; consumer reading _tail sees publish.
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int SnapshotTail() => Volatile.Read(ref _tail);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryDequeueUntil(int tailSnapshot, out RecvItem item) {
        int head = _head;
        if (head >= tailSnapshot)
        {
            item = default;
            return false;
        }

        item = _items[head & _mask];
        Volatile.Write(ref _head, head + 1);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsEmpty()
        => Volatile.Read(ref _head) >= Volatile.Read(ref _tail);

    public void Clear() {
        Volatile.Write(ref _head, 0);
        Volatile.Write(ref _tail, 0);
    }
}