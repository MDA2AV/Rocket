using System.Runtime.CompilerServices;

namespace URocket.Utils.MultiProducerSingleConsumer;

public sealed unsafe class MpscWriteItem
{
    private readonly WriteItem[] _items;
    private readonly int _mask;
    
    private long _tail; // producer-reserved count
    private long _head; // consumer position

    public MpscWriteItem(int capacityPow2) {
        if (capacityPow2 <= 0 || (capacityPow2 & (capacityPow2 - 1)) != 0)
            throw new ArgumentException("capacityPow2 must be a power of two", nameof(capacityPow2));

        _items = new WriteItem[capacityPow2];
        _mask  = capacityPow2 - 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryEnqueue(in WriteItem item) {
        // Fast full check (approx) using current head/tail
        long head = Volatile.Read(ref _head);
        long tail = Volatile.Read(ref _tail);
        if (tail - head >= _items.Length) return false; // full

        // Reserve a unique slot
        long slot = Interlocked.Increment(ref _tail) - 1;

        // Store item
        _items[slot & _mask] = item;

        // Interlocked.Increment is a full fence; consumer reading _tail sees publish.
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long SnapshotTail() => Volatile.Read(ref _tail);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryDequeueUntil(long tailSnapshot, out WriteItem item) {
        long head = _head;
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
    public bool TryDequeue(out WriteItem item) {
        long head = _head;
        if (head >= _tail)
        {
            item = default;
            return false;
        }

        item = _items[head & _mask];
        Volatile.Write(ref _head, head + 1);
        return true;
    }

    public void DequeueSingle(out WriteItem item) {
        item = _items[_head & _mask];
        Volatile.Write(ref _head, _head + 1);
    }
    
    public bool HasItems() => _head >= _tail ? true : false;

    public long GetTailHeadDiff() => _tail - _head;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsEmpty()
        => Volatile.Read(ref _head) >= Volatile.Read(ref _tail);

    public void Clear() {
        Volatile.Write(ref _head, 0);
        Volatile.Write(ref _tail, 0);
    }
}