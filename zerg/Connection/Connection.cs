using System.Buffers;
using System.Threading.Tasks.Sources;
using zerg.Utils;

namespace zerg;

public sealed partial class Connection : 
    IBufferWriter<byte>,
    IValueTaskSource<ReadResult>, 
    IValueTaskSource, /* flush */
    IDisposable
{
    /// <summary>
    /// OS socket/file descriptor (assumed to exist elsewhere in your partial class).
    /// Used by <see cref="FlushAsync"/> to enqueue flush work to the reactor.
    /// </summary>
    public int ClientFd { get; private set; }

    /// <summary>
    /// Owning reactor (used to return buffers back to reactor-owned pool).
    /// </summary>
    public Engine.Engine.Reactor Reactor { get; private set; } = null!;
    
    // =========================================================================
    // Pooling / lifecycle
    // =========================================================================

    /// <summary>
    /// Reset this instance to a safe "closed" state for reuse.
    ///
    /// Important:
    /// - Bumps <see cref="_generation"/> so any in-flight ValueTasks from a prior lifetime are invalidated.
    /// - Publishes closed=1 so late producers/consumers do not wait indefinitely.
    /// - Clears ring and resets completion state.
    /// </summary>
    public void Clear2()
    {
        // Invalidate any awaiting token by bumping generation.
        Interlocked.Increment(ref _generation);

        // Publish closed so any late handler calls don't wait.
        Volatile.Write(ref _closed, 1);

        // Write-side state (defined in another partial).
        ResetWriteBuffer();

        // Read-side state.
        Volatile.Write(ref _armed, 0);
        Volatile.Write(ref _pending, 0);
        _readSignal.Reset();
        _recv.Clear();
    }
    public void Clear()
    {
        // Invalidate any awaiting token by bumping generation.
        Interlocked.Increment(ref _generation);

        // Mark closed so late calls bail out.
        Volatile.Write(ref _closed, 1);

        // --- Wake/cancel any waiters (READ side) ---
        // If your read path can have a pending waiter, complete it with cancellation
        // (only if you have such a state; example uses _armed as "waiter armed").
        if (Interlocked.Exchange(ref _armed, 0) != 0)
        {
            try { _readSignal.SetException(new OperationCanceledException("Connection returned to pool.")); }
            catch { /* ignore: may already be completed */ }
        }

        Volatile.Write(ref _pending, 0);

        // --- Wake/cancel any waiters (FLUSH side) ---
        if (Interlocked.Exchange(ref _flushArmed, 0) != 0)
        {
            try { _flushSignal.SetException(new OperationCanceledException("Connection returned to pool.")); }
            catch { /* ignore */ }
        }

        Volatile.Write(ref _flushInProgress, 0);
        Volatile.Write(ref SendInflight, 0);

        // Write-side state
        ResetWriteBuffer();
        WriteInFlight = 0;

        // Read-side buffers
        _recv.Clear();

        // Finally reset the VTS cores for reuse
        _readSignal.Reset();
        _flushSignal.Reset();
    }

    /// <summary>
    /// Assign fd for a newly accepted connection.
    /// </summary>
    public Connection SetFd(int fd)
    {
        ClientFd = fd; 
        return this; 
    }

    /// <summary>
    /// Attach to a reactor and open the connection for a new lifetime.
    /// Must be called when taking the connection from the pool for a new client.
    /// </summary>
    public Connection SetReactor(Engine.Engine.Reactor reactor)
    {
        Reactor = reactor;

        // New live connection: open it.
        Volatile.Write(ref _closed, 0);
        Volatile.Write(ref _pending, 0);
        Volatile.Write(ref _armed, 0);
        _readSignal.Reset();
        _recv.Clear();

        return this;
    }
    
    public void Dispose()
    {
        // Free the unmanaged slab (AlignedFree)
        _manager.Free();

        // No-op for your implementation, but fine to keep for correctness/future changes.
        ((IDisposable)_manager).Dispose();
    }
}