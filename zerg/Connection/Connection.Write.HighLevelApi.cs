using System.Runtime.CompilerServices;

namespace zerg;

public partial class Connection
{
    /// <summary>
    /// Appends a managed buffer into the write slab.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe void Write(ReadOnlyMemory<byte> source)
    {
        if (Volatile.Read(ref _flushInProgress) != 0)
            throw new InvalidOperationException("Cannot write while flush is in progress.");
        
        int len = source.Length;
        if (WriteTail + len > _writeSlabSize)
            throw new InvalidOperationException("Buffer too small.");

        source.Span.CopyTo(
            new Span<byte>(WriteBuffer + WriteTail, len)
        );

        WriteTail += len;
    }
    
    /// <summary>
    /// Appends a span into the write slab.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe void Write(ReadOnlySpan<byte> source) 
    {
        if (Volatile.Read(ref _flushInProgress) != 0)
            throw new InvalidOperationException("Cannot write while flush is in progress.");
        
        int len = source.Length;
        if (WriteTail + len > _writeSlabSize)
            throw new InvalidOperationException("Buffer too small.");

        source.CopyTo(new Span<byte>(WriteBuffer + WriteTail, len));
        WriteTail += len;
    }
    
    /// <summary>
    /// Arms a flush and returns a ValueTask that completes when the reactor has flushed the current batch.
    ///
    /// Behavior:
    /// - Captures a tail snapshot (<see cref="WriteInFlight"/>) and blocks further writes until completion.
    /// - Enqueues the connection to the reactor flush queue.
    /// - Completes when reactor calls <see cref="CompleteFlush"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask FlushAsync()
    {
        // Start flush barrier (handler must not write until completion)
        if (Interlocked.Exchange(ref _flushInProgress, 1) == 1)
            throw new InvalidOperationException("FlushAsync already in progress.");

        int target = WriteTail; // single writer => plain read ok

        // Fast path: nothing to flush
        if (target == 0)
        {
            Volatile.Write(ref _flushInProgress, 0);
            return default;
        }

        // Arm single waiter
        if (Interlocked.Exchange(ref _flushArmed, 1) == 1)
            throw new InvalidOperationException("FlushAsync already armed.");

        // IMPORTANT: reset completion *for this new flush* before returning the ValueTask
        _flushSignal.Reset();

        // Publish the target for the reactor
        WriteInFlight = target;

        // Ask reactor to flush (enqueue fd)
        Reactor.EnqueueFlush(ClientFd);

        return new ValueTask(this, token: 0);
    }
}