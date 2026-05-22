using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
// ReSharper disable SuggestVarOrType_BuiltInTypes

namespace Minima.Utils;

/// <summary>
/// Bounded lock-free multi-producer / single-consumer queue of ulong.
/// CAS Vyukov variant, matching MpscUshortQueue / MpscIntQueue. Used for the
/// incremental-mode buffer-return queue, which must carry (fd, generation, bid)
/// packed into the 64-bit value so the reactor can both route the return to the
/// right connection ring and reject stale returns after a pool recycle.
/// </summary>
internal sealed class MpscUlongQueue
{
    private struct Cell
    {
        public long  Sequence;
        public ulong Value;
    }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct PaddedLong
    {
        [FieldOffset(0)] public long Value;
    }

    private readonly Cell[] _buffer;
    private readonly int    _mask;

    private PaddedLong _enqueuePos;
    private PaddedLong _dequeuePos;

    public MpscUlongQueue(int capacityPow2)
    {
        if (capacityPow2 < 2 || (capacityPow2 & (capacityPow2 - 1)) != 0)
            throw new ArgumentException("Capacity must be a power of two >= 2.", nameof(capacityPow2));

        _buffer = new Cell[capacityPow2];
        _mask   = capacityPow2 - 1;

        for (int i = 0; i < capacityPow2; i++)
            _buffer[i].Sequence = i;
    }

    // (fd, gen, bid) → ulong: fd in the high 32 bits, gen in the next 16, bid in the low 16.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Pack(int fd, ushort gen, ushort bid)
        => ((ulong)(uint)fd << 32) | ((ulong)gen << 16) | bid;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Unpack(ulong packed, out int fd, out ushort gen, out ushort bid)
    {
        fd  = (int)(packed >> 32);
        gen = (ushort)((packed >> 16) & 0xFFFF);
        bid = (ushort)(packed & 0xFFFF);
    }

    /// <summary>Multi-producer safe. Returns false if the queue is full.</summary>
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

    /// <summary>Single-consumer only. Returns false if empty.</summary>
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
}
