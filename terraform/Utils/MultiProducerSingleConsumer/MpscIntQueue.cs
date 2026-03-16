using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace terraform.Utils.MultiProducerSingleConsumer;

public sealed class MpscIntQueue
{
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct PaddedLong
    {
        [FieldOffset(0)] public long Value;
    }

    private struct Cell
    {
        public long Sequence;
        public int Value;
    }

    private readonly Cell[] _buffer;
    private readonly int _mask;

    private PaddedLong _enqueuePos;
    private PaddedLong _dequeuePos;

    public MpscIntQueue(int capacityPow2)
    {
        if (capacityPow2 <= 0 || (capacityPow2 & (capacityPow2 - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(capacityPow2), "Must be power of two.");

        _buffer = new Cell[capacityPow2];
        _mask = capacityPow2 - 1;

        for (int i = 0; i < capacityPow2; i++)
            _buffer[i].Sequence = i;

        _enqueuePos.Value = 0;
        _dequeuePos.Value = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryEnqueue(int item)
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

                continue;
            }

            if (dif < 0)
                return false;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryDequeue(out int item)
    {
        Cell[] buffer = _buffer;
        int mask = _mask;

        long pos = _dequeuePos.Value;
        ref Cell cell = ref buffer[(int)pos & mask];

        long seq = Volatile.Read(ref cell.Sequence);
        long dif = seq - (pos + 1);

        if (dif == 0)
        {
            item = cell.Value;
            _dequeuePos.Value = pos + 1;
            Volatile.Write(ref cell.Sequence, pos + mask + 1);
            return true;
        }

        item = default;
        return false;
    }

    public int CountApprox
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            long enq = Volatile.Read(ref _enqueuePos.Value);
            long deq = Volatile.Read(ref _dequeuePos.Value);
            long diff = enq - deq;
            if (diff <= 0) return 0;
            if (diff > _buffer.Length) return _buffer.Length;
            return (int)diff;
        }
    }
}
