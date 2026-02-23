using zerg.Utils.Memory;

namespace zerg;

/// <summary>
/// Thin <see cref="Stream"/> adapter over <see cref="Connection"/> for compatibility
/// with BCL pipeline APIs (e.g. <see cref="System.IO.Pipelines.PipeReader"/> /
/// <see cref="System.IO.Pipelines.PipeWriter"/>).
///
/// This stream is a *zero-copy façade*:
/// - Writes append directly into the unmanaged slab owned by <see cref="Connection"/>.
/// - Reads pull from the reactor's receive rings and copy once into the caller buffer.
/// - No internal buffering is performed here.
/// - No synchronization is performed here; the owning reactor must provide exclusivity.
///
/// This type exists purely to bridge APIs that only accept <see cref="Stream"/>.
/// Directly using <see cref="Connection"/> is always faster and preferred.
/// </summary>
public sealed class ConnectionStream : Stream
{
    /// <summary>
    /// Underlying high-performance connection.
    /// All actual I/O is delegated to this instance.
    /// </summary>
    private readonly Connection _inner;

    /// <summary>
    /// Creates a new <see cref="ConnectionStream"/> wrapper over the given connection.
    /// </summary>
    public ConnectionStream(Connection inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    // -----------------------------------------------------------------
    // Write Path
    // -----------------------------------------------------------------

    /// <summary>
    /// Appends the provided bytes into the connection's unmanaged write slab.
    /// This does NOT flush; flushing is controlled by the reactor.
    /// </summary>
    public override void Write(byte[] buffer, int offset, int count)
    {
        if (buffer is null) throw new ArgumentNullException(nameof(buffer));
        if ((uint)offset > (uint)buffer.Length) throw new ArgumentOutOfRangeException(nameof(offset));
        if ((uint)count > (uint)(buffer.Length - offset)) throw new ArgumentOutOfRangeException(nameof(count));
        if (count == 0) return;

        // Forward directly to the connection's hot write path.
        _inner.Write(buffer.AsSpan(offset, count));
    }

    /// <summary>
    /// Fast async write path used by <see cref="System.IO.Pipelines.PipeWriter"/>.
    /// No allocation, no implicit flush, no async state machine.
    /// </summary>
    public override ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0)
            return ValueTask.CompletedTask;

        _inner.Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Flushes all data previously written into the slab.
    /// The reactor controls the actual send; this only signals intent.
    /// </summary>
    public override Task FlushAsync(CancellationToken token)
        => _inner.FlushAsync().AsTask();

    // -----------------------------------------------------------------
    // Read Path
    // -----------------------------------------------------------------

    /// <summary>
    /// Async read bridge for legacy array APIs.
    /// </summary>
    public override Task<int> ReadAsync(
        byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <summary>
    /// Reads from the reactor receive rings and copies the data into the destination.
    ///
    /// This method:
    /// - Awaits the next receive snapshot.
    /// - Copies all contiguous ring segments into <paramref name="destination"/>.
    /// - Returns each ring to the pool immediately after copy.
    /// </summary>
    public override async ValueTask<int> ReadAsync(
        Memory<byte> destination,
        CancellationToken cancellationToken = default)
    {
        if (destination.Length == 0)
            return 0;

        // Await a receive snapshot from the reactor.
        var result = await _inner.ReadAsync();
        if (result.IsClosed)
            return 0;

        // Gather all segments that belong to this snapshot.
        var rings = _inner.GetAllSnapshotRings(result);

        // Copy once into the caller buffer.
        var len = destination.CopyFromRings(rings);

        // Return ring buffers back to the reactor pool.
        foreach (var ring in rings)
            _inner.ReturnRing(ring.BufferId);

        return len;
    }

    // -----------------------------------------------------------------
    // Lifetime
    // -----------------------------------------------------------------

    private int _disposed;

    /// <summary>
    /// Disposes the underlying connection.
    /// This method is idempotent and safe to call multiple times.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            base.Dispose(disposing);
            return;
        }

        _inner.Dispose();
        base.Dispose(disposing);
    }

    // -----------------------------------------------------------------
    // Unsupported Stream Surface
    // -----------------------------------------------------------------

    /// <summary>
    /// Synchronous reads are not supported.
    /// This prevents accidental slow paths in pipeline adapters.
    /// </summary>
    public override int Read(byte[] buffer, int offset, int count)
        => throw new NotSupportedException();

    /// <summary>
    /// Synchronous flush is not supported; use <see cref="FlushAsync"/>.
    /// </summary>
    public override void Flush()
        => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin)
        => throw new NotSupportedException();

    public override void SetLength(long value)
        => throw new NotSupportedException();

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;

    public override long Length
        => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }
}
