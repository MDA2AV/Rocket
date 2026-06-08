using System.Runtime.InteropServices;
using Kingslayer.Utils;

namespace Kingslayer;

public sealed unsafe partial class Connection
{
    private readonly Reactor _reactor;

    public int ClientFd { get; private set; }

    // Bumped on Clear(); low 16 bits used as the IVTS token so stale awaiters are detectable after reuse.
    private int _generation;

    // Refcount over the two owners (reactor + handler). Init to 2 on accept; teardown runs at refs==0.
    // With the per-reactor SynchronizationContext the handler always resumes on the reactor, so both
    // DecRef sites run on the reactor thread and Recycle can be called directly (no recycle queue).
    private int _refs;

    public Connection(Reactor reactor, int fd, int writeSlabSize = 1024 * 16)
    {
        _reactor = reactor;
        ClientFd = fd;
        _writeSlabSize = writeSlabSize;
        WriteBuffer = (byte*)NativeMemory.AlignedAlloc((nuint)writeSlabSize, 64);
        _manager = new UnmanagedMemoryManager(WriteBuffer, writeSlabSize);
    }

    public void MarkClosed()
    {
        Volatile.Write(ref _closed, 1);

        if (Interlocked.Exchange(ref _armed, 0) == 1)
        {
            _readSignal.SetResult(new RecvSnapshot(_recv.SnapshotTail(), isClosed: true));
        }
        else
        {
            Volatile.Write(ref _pending, 1);
        }

        if (Interlocked.Exchange(ref _flushArmed, 0) == 1)
        {
            Volatile.Write(ref _flushInProgress, 0);
            _flushSignal.SetResult(true);
        }
    }

    internal void InitRefs() => Volatile.Write(ref _refs, 2);

    internal void DecRef()
    {
        // Both owners release on the reactor thread, so teardown is a direct call — no handoff queue.
        if (Interlocked.Decrement(ref _refs) == 0)
        {
            _reactor.Recycle(this, ClientFd);
        }
    }

    /// <summary>Hand a consumed recv buffer back to this reactor's shared buf_ring. Called from the
    /// handler, which always runs on the reactor thread, so it's a direct return (no return queue).</summary>
    public void ReturnBuffer(in SpscRecvRing.Item item) => _reactor.ReturnBufferDirect(item.Bid);

    internal void Clear()
    {
        Interlocked.Increment(ref _generation);

        Volatile.Write(ref _armed, 0);
        Volatile.Write(ref _pending, 0);
        Volatile.Write(ref _closed, 0);
        Volatile.Write(ref _flushArmed, 0);
        Volatile.Write(ref _flushInProgress, 0);

        WriteHead = 0;
        WriteTail = 0;
        WriteInFlight = 0;

        _readSignal.Reset();
        _flushSignal.Reset();

        _recv.Reset();
    }

    public void Dispose()
    {
        if (WriteBuffer != null)
        {
            NativeMemory.AlignedFree(WriteBuffer);
            WriteBuffer = null;
        }
    }
}
