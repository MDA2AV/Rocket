using System.Runtime.CompilerServices;

namespace zerg.core;

internal sealed class HeldBufferRing
{
    private HeldBuffer[] _items;
    private int _head;
    private int _count;
    private int _mask;

    public HeldBufferRing(int initialCapacity)
    {
        int cap = RoundUpPow2(initialCapacity);
        _items = new HeldBuffer[cap];
        _mask = cap - 1;
    }

    public int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _count;
    }

    public ref HeldBuffer this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref _items[(_head + index) & _mask];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PushBack(in HeldBuffer item)
    {
        if (_count == _items.Length)
            Grow();

        _items[(_head + _count) & _mask] = item;
        _count++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PopFront()
    {
        _items[_head] = default;
        _head = (_head + 1) & _mask;
        _count--;
    }

    public void Clear()
    {
        for (int i = 0; i < _count; i++)
            _items[(_head + i) & _mask] = default;

        _head = 0;
        _count = 0;
    }

    private void Grow()
    {
        int newCap = _items.Length * 2;
        HeldBuffer[] newItems = new HeldBuffer[newCap];

        for (int i = 0; i < _count; i++)
            newItems[i] = _items[(_head + i) & _mask];

        _items = newItems;
        _mask = newCap - 1;
        _head = 0;
    }

    private static int RoundUpPow2(int value)
    {
        int cap = 1;
        while (cap < value) cap <<= 1;
        return cap;
    }

    internal readonly record struct HeldBuffer(ReadOnlyMemory<byte> Memory, ushort BufferId);
}
