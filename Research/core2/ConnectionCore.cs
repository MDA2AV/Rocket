using System.Buffers;
using System.Threading.Tasks.Sources;

namespace core2;

/*
public abstract unsafe class ConnectionCore :
    IValueTaskSource<RingSnapshot>
{
    public int ClientFd { get; protected set; }
    public IReactor Reactor { get; protected set; } = null!;
    
    private ManualResetValueTaskSourceCore<RingSnapshot> _readSignal;
    private int _armed;
    private int _pending;
    private int _closed;
    private int _generation;
    
    public virtual void Clear()
    {
        Interlocked.Increment(ref _generation);
        Volatile.Write(ref _closed, 1);

        if (Interlocked.Exchange(ref _armed, 0) != 0)
        {
            try { _readSignal.SetException(new OperationCanceledException("Connection returned to pool.")); }
            catch
            {
                // ignored
            }
        }

        Volatile.Write(ref _pending, 0);

        if (Interlocked.Exchange(ref _flushArmed, 0) != 0)
        {
            try { _flushSignal.SetException(new OperationCanceledException("Connection returned to pool.")); }
            catch
            {
                // ignored
            }
        }

        Volatile.Write(ref _flushInProgress, 0);
        Volatile.Write(ref SendInflight, 0);

        ResetWriteBuffer();
        WriteInFlight = 0;

        _recv.Clear();

        _readSignal.Reset();
        _flushSignal.Reset();
    }

    public ConnectionCore SetFd(int fd)
    {
        ClientFd = fd;
        return this;
    }

    public ConnectionCore SetReactor(IReactor reactor)
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
*/