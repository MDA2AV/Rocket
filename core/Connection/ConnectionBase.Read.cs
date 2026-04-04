using System.Runtime.CompilerServices;
using System.Threading.Tasks.Sources;

namespace zerg.core;

[SkipLocalsInit]
public abstract partial class ConnectionBase
{
    private ManualResetValueTaskSourceCore<RingSnapshot> _readSignal;
    private int _armed;
    private int _pending;
    private int _closed;
    private int _generation;

    private readonly SpscRecvRing _recv = new(capacityPow2: 1024);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void MarkClosed(int error = 0)
    {
        Volatile.Write(ref _closed, 1);

        if (Interlocked.Exchange(ref _armed, 0) == 1)
            _readSignal.SetResult(RingSnapshot.Closed(error));
        else
            Volatile.Write(ref _pending, 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<RingSnapshot> ReadAsync()
    {
        if (Volatile.Read(ref _closed) != 0)
            return new ValueTask<RingSnapshot>(RingSnapshot.Closed());

        if (Volatile.Read(ref _pending) == 1 || !_recv.IsEmpty())
        {
            Volatile.Write(ref _pending, 0);

            if (Volatile.Read(ref _closed) != 0)
                return new ValueTask<RingSnapshot>(RingSnapshot.Closed());

            long snap = _recv.SnapshotTail();
            SnapshotRingCount = (int)(snap - _recv.Head);

            return new ValueTask<RingSnapshot>(new RingSnapshot(snap, isClosed: false));
        }

        if (Interlocked.Exchange(ref _armed, 1) == 1)
            throw new InvalidOperationException("ReadAsync already armed.");

        int gen = Volatile.Read(ref _generation);

        if (Volatile.Read(ref _closed) != 0)
        {
            Interlocked.Exchange(ref _armed, 0);
            return new ValueTask<RingSnapshot>(RingSnapshot.Closed());
        }

        return new ValueTask<RingSnapshot>(this, (short)gen);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ResetRead()
    {
        _readSignal.Reset();

        if (!_recv.IsEmpty())
            Volatile.Write(ref _pending, 1);

        if (Volatile.Read(ref _closed) != 0)
            Volatile.Write(ref _pending, 1);
    }

    // =========================================================================
    // IValueTaskSource<RingSnapshot> plumbing
    // =========================================================================

    RingSnapshot IValueTaskSource<RingSnapshot>.GetResult(short token)
    {
        if (token != (short)Volatile.Read(ref _generation))
            return RingSnapshot.Closed();

        return _readSignal.GetResult(_readSignal.Version);
    }

    ValueTaskSourceStatus IValueTaskSource<RingSnapshot>.GetStatus(short token)
    {
        if (token != (short)Volatile.Read(ref _generation))
            return ValueTaskSourceStatus.Succeeded;

        return _readSignal.GetStatus(_readSignal.Version);
    }

    void IValueTaskSource<RingSnapshot>.OnCompleted(
        Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
    {
        if (token != (short)Volatile.Read(ref _generation))
        {
            continuation(state);
            return;
        }

        _readSignal.OnCompleted(continuation, state, _readSignal.Version, flags);
    }
}
