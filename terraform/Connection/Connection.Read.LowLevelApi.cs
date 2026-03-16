using System.Runtime.CompilerServices;
using terraform.Utils;

namespace terraform;

public sealed unsafe partial class Connection
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetRing(long tailSnapshot, out RingItem item)
        => _recv.TryDequeueUntil(tailSnapshot, out item);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RingItem GetRing() => _recv.DequeueSingle();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EnqueueRingItem(byte* ptr, int length, ushort bufferId)
    {
        if (Volatile.Read(ref _closed) != 0)
            return;

        if (!_recv.TryEnqueue(new RingItem(ptr, length, bufferId)))
        {
            Volatile.Write(ref _closed, 1);

            if (Interlocked.Exchange(ref _armed, 0) == 1)
                _readSignal.SetResult(RingSnapshot.Closed(error: 0));
            else
                Volatile.Write(ref _pending, 1);

            return;
        }

        if (Interlocked.Exchange(ref _armed, 0) == 1)
        {
            long snap = _recv.SnapshotTail();
            SnapshotRingCount = (int)(snap - _recv.Head);
            _readSignal.SetResult(new RingSnapshot(snap, isClosed: false));
        }
        else
        {
            Volatile.Write(ref _pending, 1);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReturnRing(ushort bufferId)
    {
        Reactor.EnqueueReturnQ(bufferId);
    }
}
