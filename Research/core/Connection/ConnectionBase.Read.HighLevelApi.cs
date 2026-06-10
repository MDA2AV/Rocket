using System.Buffers;

namespace zerg.core;

public abstract partial class ConnectionBase
{
    public long TotalRingCount => _recv.GetTailHeadDiff();
    public int SnapshotRingCount { get; protected set; }

    public bool TryDynamicallyGetAllSnapshotRingsAsReadOnlySequence(RingSnapshot readResult, out List<UnmanagedMemoryManager> rings, out ReadOnlySequence<byte> sequence)
    {
        rings = null!;
        var tailSnapshot = readResult.TailSnapshot;

        if (!_recv.TryDequeueUntil(tailSnapshot, out var headItem))
        {
            sequence = default;
            return false;
        }

        var innerRings = new List<UnmanagedMemoryManager>(2);
        rings = innerRings;

        var headMem = headItem.AsUnmanagedMemoryManager();
        var head = new RingSegment(headMem.Memory, headMem.BufferId);
        innerRings.Add(headMem);
        var tail = head;

        while (_recv.TryDequeueUntil(tailSnapshot, out var item))
        {
            var mem = item.AsUnmanagedMemoryManager();
            tail = tail.Append(mem.Memory, mem.BufferId);
            innerRings.Add(mem);
        }

        sequence = new ReadOnlySequence<byte>(head, 0, tail, tail.Memory.Length);
        return true;
    }

    public bool TryDynamicallyGetAllSnapshotRingsAsUnmanagedMemory(RingSnapshot readResult, out List<UnmanagedMemoryManager> rings)
    {
        rings = null!;
        var tailSnapshot = readResult.TailSnapshot;

        if (!_recv.TryDequeueUntil(tailSnapshot, out var headItem))
            return false;

        var innerRings = new List<UnmanagedMemoryManager>(2);
        rings = innerRings;

        innerRings.Add(headItem.AsUnmanagedMemoryManager());

        while (_recv.TryDequeueUntil(tailSnapshot, out var item))
            innerRings.Add(item.AsUnmanagedMemoryManager());

        return true;
    }

    public bool TryDynamicallyGetAllSnapshotRings(RingSnapshot readResult, out List<RingItem> rings)
    {
        rings = null!;
        var tailSnapshot = readResult.TailSnapshot;

        if (!_recv.TryDequeueUntil(tailSnapshot, out var headItem))
            return false;

        var innerRings = new List<RingItem>(2);
        rings = innerRings;

        innerRings.Add(headItem);

        while (_recv.TryDequeueUntil(tailSnapshot, out var item))
            innerRings.Add(item);

        return true;
    }

    public UnmanagedMemoryManager[] GetAllSnapshotRingsAsUnmanagedMemory(RingSnapshot readResult)
    {
        var count = SnapshotRingCount;

        if (count == 1)
            return [GetRing().AsUnmanagedMemoryManager()];

        var mems = new UnmanagedMemoryManager[count];
        for (int i = 0; i < count; i++)
            mems[i] = GetRing().AsUnmanagedMemoryManager();

        return mems;
    }

    public RingItem[] GetAllSnapshotRings(RingSnapshot readResult)
    {
        var count = SnapshotRingCount;

        if (count == 1)
            return [GetRing()];

        var items = new RingItem[count];
        for (int i = 0; i < count; i++)
            items[i] = GetRing();

        return items;
    }
}
