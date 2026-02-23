using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace zerg.Utils.MultiProducerSingleConsumer;

/// <summary>
/// Multi-producer single-consumer queue for int.
/// Lock-free, bounded, power-of-two capacity.
/// </summary>
/// <remarks>
/// Algorithm: Dmitry Vyukov's bounded MPMC queue, specialized to MPSC usage.
/// - Producers claim slots with an atomic enqueue position.
/// - Consumer dequeues in order with a single dequeue position.
/// - Each slot has a sequence number to coordinate ownership.
///
/// Properties:
/// - No allocations after construction.
/// - Wait-free on success paths, lock-free overall.
/// - Correct under multiple producers, single consumer.
/// </remarks>
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

    // Producer-side position (multiple writers) and consumer-side position (single reader).
    private PaddedLong _enqueuePos;
    private PaddedLong _dequeuePos;

    /// <summary>
    /// Create a new queue with capacity = 2^capacityPow2.
    /// </summary>
    public MpscIntQueue(int capacityPow2)
    {
        // capacityPow2 is the actual capacity, must be power of two
        if (capacityPow2 <= 0 || (capacityPow2 & (capacityPow2 - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(capacityPow2), "Must be power of two.");

        _buffer = new Cell[capacityPow2];
        _mask = capacityPow2 - 1;

        for (int i = 0; i < capacityPow2; i++)
            _buffer[i].Sequence = i;

        _enqueuePos.Value = 0;
        _dequeuePos.Value = 0;
    }

    /// <summary>Returns false if the queue is full.</summary>
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
                // Try to claim this slot.
                if (Interlocked.CompareExchange(ref _enqueuePos.Value, pos + 1, pos) == pos)
                {
                    cell.Value = item;
                    // Publish: make slot visible to consumer.
                    Volatile.Write(ref cell.Sequence, pos + 1);
                    return true;
                }

                // Lost the race; retry.
                continue;
            }

            if (dif < 0)
            {
                // Slot not yet consumed => queue full.
                return false;
            }

            // Another producer is ahead; retry with refreshed position.
        }
    }

    /// <summary>
    /// Dequeue one item. Single-consumer only.
    /// Returns false if empty.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryDequeue(out int item)
    {
        Cell[] buffer = _buffer;
        int mask = _mask;

        long pos = _dequeuePos.Value; // single-consumer: plain read ok
        ref Cell cell = ref buffer[(int)pos & mask];

        long seq = Volatile.Read(ref cell.Sequence);
        long dif = seq - (pos + 1);

        if (dif == 0)
        {
            item = cell.Value;

            // Advance consumer position (single consumer: plain write ok).
            _dequeuePos.Value = pos + 1;

            // Mark slot as free for producers.
            Volatile.Write(ref cell.Sequence, pos + mask + 1);
            return true;
        }

        item = default;
        return false;
    }

    /// <summary>
    /// Best-effort count (may be approximate under contention).
    /// </summary>
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