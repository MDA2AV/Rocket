using System.Runtime.InteropServices;
using KestrelMinima.Utils;

namespace KestrelMinima;

public sealed unsafe partial class Connection
{
    private readonly Reactor _reactor;

    public int ClientFd { get; private set; }

    // Bumped on Clear(); the low 16 bits are used as the read IVTS token so
    // stale awaiters can be detected after pool reuse. (The Kestrel path never
    // touches the read IVTS, but it's reused by MarkClosed's `_readSignal`
    // SetResult — harmless when nobody awaits.)
    private int _generation;

    public Connection(Reactor reactor, int fd, int writeSlabSize = 256 * 1024)
    {
        _reactor = reactor;
        ClientFd = fd;
        _writeSlabSize = writeSlabSize;
        WriteBuffer = (byte*)NativeMemory.AlignedAlloc((nuint)writeSlabSize, 64);

        _manager = new UnmanagedMemoryManager(WriteBuffer, writeSlabSize);
    }

    // Reactor-thread only — called from Recycle in the reactor's recv/send error paths.
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
    }

    internal void Clear()
    {
        Interlocked.Increment(ref _generation);

        Volatile.Write(ref _armed, 0);
        Volatile.Write(ref _pending, 0);
        Volatile.Write(ref _closed, 0);

        WriteHead = 0;
        WriteSubmitted = 0;
        WriteTail = 0;

        _readSignal.Reset();

        _recv.Reset();             // discard any leftover SPSC items
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
