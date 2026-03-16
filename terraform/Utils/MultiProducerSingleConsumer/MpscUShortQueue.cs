using System.Runtime.CompilerServices;

namespace terraform.Utils.MultiProducerSingleConsumer;

public sealed class MpscUshortQueue
{
    private readonly int _capacity;
    private readonly int _mask;

    private readonly long[] _seq;
    private readonly ushort[] _data;

    private long _tail;
    private long _head;

    public MpscUshortQueue(int capacityPowerOfTwo)
    {
        if (capacityPowerOfTwo < 2 || (capacityPowerOfTwo & (capacityPowerOfTwo - 1)) != 0)
            throw new ArgumentException("Capacity must be a power of two >= 2.", nameof(capacityPowerOfTwo));

        _capacity = capacityPowerOfTwo;
        _mask = capacityPowerOfTwo - 1;

        _seq  = new long[_capacity];
        _data = new ushort[_capacity];

        for (int i = 0; i < _capacity; i++)
            _seq[i] = i;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryEnqueue(ushort value)
    {
        long ticket = Interlocked.Increment(ref _tail) - 1;
        int  idx    = (int)(ticket & _mask);

        long seq = Volatile.Read(ref _seq[idx]);
        if (seq != ticket)
            return false;

        _data[idx] = value;
        Volatile.Write(ref _seq[idx], ticket + 1);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EnqueueSpin(ushort value)
    {
        SpinWait sw = default;
        while (!TryEnqueue(value))
            sw.SpinOnce();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryDequeue(out ushort value)
    {
        long head = _head;
        int  idx  = (int)(head & _mask);

        long seq = Volatile.Read(ref _seq[idx]);
        if (seq != head + 1)
        {
            value = default;
            return false;
        }

        value = _data[idx];
        Volatile.Write(ref _seq[idx], head + _capacity);
        _head = head + 1;
        return true;
    }

    public int Drain(Action<ushort> consume, int max = int.MaxValue)
    {
        int n = 0;
        while (n < max && TryDequeue(out ushort v))
        {
            consume(v);
            n++;
        }
        return n;
    }
}
