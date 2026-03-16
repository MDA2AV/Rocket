using System.Buffers;
using System.Threading.Tasks.Sources;

namespace Zerg.Core;

public abstract unsafe partial class ConnectionBase :
    IBufferWriter<byte>,
    IValueTaskSource<RingSnapshot>,
    IValueTaskSource,
    IDisposable
{
    public int ClientFd { get; protected set; }
    public IReactor Reactor { get; protected set; } = null!;

    // =========================================================================
    // Pooling / lifecycle
    // =========================================================================

    public virtual void Clear()
    {
        Interlocked.Increment(ref _generation);
        Volatile.Write(ref _closed, 1);

        if (Interlocked.Exchange(ref _armed, 0) != 0)
        {
            try { _readSignal.SetException(new OperationCanceledException("Connection returned to pool.")); }
            catch { }
        }

        Volatile.Write(ref _pending, 0);

        if (Interlocked.Exchange(ref _flushArmed, 0) != 0)
        {
            try { _flushSignal.SetException(new OperationCanceledException("Connection returned to pool.")); }
            catch { }
        }

        Volatile.Write(ref _flushInProgress, 0);
        Volatile.Write(ref SendInflight, 0);

        ResetWriteBuffer();
        WriteInFlight = 0;

        _recv.Clear();

        _readSignal.Reset();
        _flushSignal.Reset();
    }

    public ConnectionBase SetFd(int fd)
    {
        ClientFd = fd;
        return this;
    }

    public ConnectionBase SetReactor(IReactor reactor)
    {
        Reactor = reactor;
        Volatile.Write(ref _closed, 0);
        Volatile.Write(ref _pending, 0);
        Volatile.Write(ref _armed, 0);
        _readSignal.Reset();
        _recv.Clear();
        return this;
    }

    public virtual void Dispose()
    {
        _manager.Free();
        ((IDisposable)_manager).Dispose();
    }
}
