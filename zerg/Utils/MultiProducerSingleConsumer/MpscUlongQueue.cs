using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace zerg.Utils.MultiProducerSingleConsumer;

/// <summary>
/// Multi-producer single-consumer queue for ulong.
/// Lock-free, bounded, power-of-two capacity.
/// Used to carry packed (fd, bid) pairs for incremental buffer returns.
/// </summary>
public sealed class MpscUlongQueue
{
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct PaddedLong
    {
        [FieldOffset(0)] public long Value;
    }

    private struct Cell
    {
        public long Sequence;
        public ulong Value;
    }

    private readonly Cell[] _buffer;
    private readonly int _mask;

    private PaddedLong _enqueuePos;
    private PaddedLong _dequeuePos;

    public MpscUlongQueue(int capacityPow2)
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
    public bool TryEnqueue(ulong item)
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
    public bool TryDequeue(out ulong item)
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Pack(int fd, ushort bid) => ((ulong)(uint)fd << 16) | bid;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Unpack(ulong packed, out int fd, out ushort bid)
    {
        fd = (int)(uint)(packed >> 16);
        bid = (ushort)(packed & 0xFFFF);
    }
}
