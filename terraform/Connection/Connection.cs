using System.Buffers;
using System.Runtime.InteropServices;
using System.Threading.Tasks.Sources;
using terraform.Utils;

namespace terraform;

public sealed partial class Connection :
    IBufferWriter<byte>,
    IValueTaskSource<RingSnapshot>,
    IValueTaskSource,
    IDisposable
{
    public int ClientFd { get; private set; }
    public Engine.Engine.Reactor Reactor { get; private set; } = null!;

    // =========================================================================
    // Pooling / lifecycle
    // =========================================================================

    public void Clear()
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

    public Connection SetFd(int fd)
    {
        ClientFd = fd;
        return this;
    }

    public Connection SetReactor(Engine.Engine.Reactor reactor)
    {
        Reactor = reactor;
        Volatile.Write(ref _closed, 0);
        Volatile.Write(ref _pending, 0);
        Volatile.Write(ref _armed, 0);
        _readSignal.Reset();
        _recv.Clear();
        return this;
    }

    public unsafe void Dispose()
    {
        _manager.Free();
        ((IDisposable)_manager).Dispose();
    }
}
