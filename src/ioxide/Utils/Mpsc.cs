using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
// ReSharper disable SuggestVarOrType_BuiltInTypes

namespace ioxide.utils;

/// <summary>
/// Bounded lock-free MPSC queue - Vyukov's bounded MPMC specialised to one consumer.
/// Power-of-two capacity, zero allocation after construction; slot sequence numbers
/// coordinate ownership. T is unmanaged so cells are blittable.
/// </summary>
internal sealed class Mpsc<T> where T : unmanaged
{
    private struct Cell
    {
        public long Sequence;
        public T    Value;
    }

    private readonly Cell[] _buffer;
    private readonly int    _mask;

    private PaddedLong _enqueuePos;
    private PaddedLong _dequeuePos;

    public Mpsc(int capacityPow2)
    {
        if (capacityPow2 < 2 || (capacityPow2 & (capacityPow2 - 1)) != 0)
            throw new ArgumentException("Capacity must be a power of two >= 2.", nameof(capacityPow2));

        _buffer = new Cell[capacityPow2];
        _mask   = capacityPow2 - 1;

        for (int i = 0; i < capacityPow2; i++)
            _buffer[i].Sequence = i;
    }

    /// <summary>Multi-producer safe. Returns false if the queue is full.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryEnqueue(T item)
    {
        Cell[] buffer = _buffer;
        int mask = _mask;

        while (true)
        {
            long pos = Volatile.Read(ref _enqueuePos.Value);
            ref Cell cell = ref buffer[(int)pos & mask];

            long seq = Volatile.Read(ref cell.Sequence);
            long dif = seq - pos;

            if (dif == 0)
            {
                if (Interlocked.CompareExchange(ref _enqueuePos.Value, pos + 1, pos) == pos)
                {
                    cell.Value = item;
                    Volatile.Write(ref cell.Sequence, pos + 1);
                    return true;
                }
                continue;   // lost the race; reload and retry
            }

            if (dif < 0)
                return false;   // slot not yet consumed → full
        }
    }

    /// <summary>Single-consumer only. Returns false if empty.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryDequeue(out T item)
    {
        Cell[] buffer = _buffer;
        int mask = _mask;

        long pos = _dequeuePos.Value;   // single consumer: plain read
        ref Cell cell = ref buffer[(int)pos & mask];

        long seq = Volatile.Read(ref cell.Sequence);
        long dif = seq - (pos + 1);

        if (dif == 0)
        {
            item = cell.Value;
            _dequeuePos.Value = pos + 1;                          // single consumer: plain write
            Volatile.Write(ref cell.Sequence, pos + mask + 1);   // free slot for producers
            return true;
        }

        item = default;
        return false;
    }
}

// Padded to a cache line (no false sharing). Top-level: the CLR forbids explicit
// layout on a type nested inside a generic.
[StructLayout(LayoutKind.Explicit, Size = 64)]
internal struct PaddedLong
{
    [FieldOffset(0)] public long Value;
}
