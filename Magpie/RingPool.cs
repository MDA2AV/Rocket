using System.Collections.Concurrent;

namespace Magpie;

/// <summary>
/// Bounded pool of small plain rings. A caller borrows a ring for one submit+wait+reap,
/// then returns it — a ring is used by one thread at a time, never concurrently. The
/// first ring owns the io-wq; the rest attach to it (<see cref="Native.IORING_SETUP_ATTACH_WQ"/>)
/// so cache-miss reads share one kernel worker pool. Pool size bounds in-flight reads;
/// extra callers wait on the gate until a ring frees.
/// </summary>
public sealed class RingPool : IDisposable
{
    private readonly ConcurrentStack<Ring> _free = new();
    private readonly Ring[] _all;
    private readonly SemaphoreSlim _gate;

    public RingPool(int size, uint depth)
    {
        if (size <= 0) size = Environment.ProcessorCount;

        _all = new Ring[size];
        _all[0] = Ring.Create(depth, -1);              // primary owns the shared io-wq
        for (int i = 1; i < size; i++)
            _all[i] = Ring.Create(depth, _all[0].Fd);  // attach to primary's wq

        foreach (Ring r in _all) _free.Push(r);
        _gate = new SemaphoreSlim(size, size);
    }

    public Ring Rent()
    {
        _gate.Wait();
        _free.TryPop(out Ring? r);
        return r!;
    }

    public void Return(Ring r)
    {
        _free.Push(r);
        _gate.Release();
    }

    public void Dispose()
    {
        foreach (Ring r in _all) r.Dispose();
        _gate.Dispose();
    }
}
