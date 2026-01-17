using System.Runtime.CompilerServices;

namespace URocket.Utils.MultiProducerSingleConsumer;

/// <summary>
/// ushort Multi Producer Single Consumer Queue
/// </summary>
public sealed class MpscUshortQueue {
    // Capacity must be a power of two
    private readonly int _capacity;
    private readonly int _mask;

    private readonly long[] _seq;
    private readonly ushort[] _data;

    // Producers increment tail; consumer increments head
    private long _tail;
    private long _head;

    public MpscUshortQueue(int capacityPowerOfTwo) {
        if (capacityPowerOfTwo < 2 || (capacityPowerOfTwo & (capacityPowerOfTwo - 1)) != 0)
            throw new ArgumentException("Capacity must be a power of two >= 2.", nameof(capacityPowerOfTwo));

        _capacity = capacityPowerOfTwo;
        _mask = capacityPowerOfTwo - 1;

        _seq  = new long[_capacity];
        _data = new ushort[_capacity];

        // Initialize sequence numbers so slot i is "free for ticket i"
        for (int i = 0; i < _capacity; i++)
            _seq[i] = i;
    }

    /// <summary>
    /// Try to enqueue. Returns false if full right now.
    /// Multi-producer safe.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryEnqueue(ushort value) {
        // Reserve a ticket (unique position) among all producers
        long ticket = Interlocked.Increment(ref _tail) - 1;
        int  idx    = (int)(ticket & _mask);

        // Slot is free when seq[idx] == ticket
        long seq = Volatile.Read(ref _seq[idx]);
        if (seq != ticket)
        {
            // Queue is full (or producer is too far ahead) -> fail fast
            return false;
        }

        _data[idx] = value;
        // Publish: mark slot as ready for consumer (seq = ticket + 1)
        Volatile.Write(ref _seq[idx], ticket + 1);
        return true;
    }

    /// <summary>
    /// Enqueue, spinning until space is available.
    /// Use this when dropping is not acceptable.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EnqueueSpin(ushort value) {
        SpinWait sw = default;
        while (!TryEnqueue(value))
            sw.SpinOnce();
    }

    /// <summary>
    /// Try to dequeue. Returns false if empty.
    /// Single-consumer only.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryDequeue(out ushort value) {
        long head = _head;
        int  idx  = (int)(head & _mask);

        // Slot is ready when seq[idx] == head + 1
        long seq = Volatile.Read(ref _seq[idx]);
        if (seq != head + 1)
        {
            value = default;
            return false; // empty
        }

        value = _data[idx];

        // Mark slot as free for the next wrap:
        // next expected free ticket for this idx is head + capacity
        Volatile.Write(ref _seq[idx], head + _capacity);

        _head = head + 1;
        return true;
    }

    /// <summary>Drain up to 'max' items. Returns number drained.</summary>
    public int Drain(Action<ushort> consume, int max = int.MaxValue) {
        int n = 0;
        while (n < max && TryDequeue(out ushort v))
        {
            consume(v);
            n++;
        }
        return n;
    }
}
