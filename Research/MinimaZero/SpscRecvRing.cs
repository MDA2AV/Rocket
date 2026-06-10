using System.Runtime.CompilerServices;
// ReSharper disable SuggestVarOrType_BuiltInTypes

namespace MinimaZero;

internal sealed unsafe class SpscRecvRing
{
    public struct Item
    {
        public byte* Ptr;     // _area + (Off & ~IORING_ZCRX_AREA_MASK)
        public ulong Off;     // full rcqe.off token (carries area id in high bits)
        public int   Len;     // bytes the NIC DMA'd into this chunk (cqe.res)
        public bool  HasBuffer;

        public ReadOnlySpan<byte> AsSpan() => new(Ptr, Len);

        public UnmanagedMemoryManager AsMemoryManager() => new(Ptr, Len, Off);
    }

    private readonly Item[] _items;
    private readonly int _mask;
    private long _tail;
    private long _head;

    public SpscRecvRing(int capacityPow2)
    {
        if (capacityPow2 <= 0 || (capacityPow2 & (capacityPow2 - 1)) != 0)
        {
            throw new ArgumentException("capacity must be a power of two", nameof(capacityPow2));
        }

        _items = new Item[capacityPow2];
        _mask  = capacityPow2 - 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryEnqueue(in Item item)
    {
        long head = Volatile.Read(ref _head);
        long tail = _tail;

        if ((ulong)(tail - head) >= (ulong)_items.Length)
        {
            return false;
        }

        _items[(int)(tail & _mask)] = item;
        Volatile.Write(ref _tail, tail + 1);

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryDequeue(out Item item)
    {
        long head = _head;
        long tail = Volatile.Read(ref _tail);

        if (head >= tail)
        {
            item = default;
            return false;
        }

        item = _items[(int)(head & _mask)];
        Volatile.Write(ref _head, head + 1);

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long SnapshotTail() => Volatile.Read(ref _tail);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryDequeueUntil(long tailSnapshot, out Item item)
    {
        long head = _head;

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
    public bool IsEmpty() => Volatile.Read(ref _head) >= Volatile.Read(ref _tail);
}
