using URocket.Utils.Memory;

namespace URocket.Connection;

public class ConnectionStream : Stream
{
    private readonly Connection _inner;
    public ConnectionStream(Connection inner)
    {
        _inner = inner;
    }
    public override void Write(byte[] buffer, int offset, int count)
    {
        if (buffer is null) throw new ArgumentNullException(nameof(buffer));
        if ((uint)offset > (uint)buffer.Length) throw new ArgumentOutOfRangeException(nameof(offset));
        if ((uint)count > (uint)(buffer.Length - offset)) throw new ArgumentOutOfRangeException(nameof(count));
        if (count == 0) return;

        // Append into the unmanaged write slab
        _inner.InnerWrite(buffer.AsSpan(offset, count));
    }
    
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        _inner.InnerWrite(buffer.Span);          // no implicit flush
        return ValueTask.CompletedTask;
    }
    
    public override Task FlushAsync(CancellationToken token) => _inner.InnerFlushAsync().AsTask();

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> destination,
        CancellationToken cancellationToken = default)
    {
        if (destination.Length == 0) 
            return 0;

        var result = await _inner.ReadAsync();
        if (result.IsClosed)
            return 0;
        
        var rings = _inner.GetAllSnapshotRings(result);
        var len = destination.CopyFromRings(rings);

        foreach (var ring in rings)
        {
            _inner.ReturnRing(ring.BufferId);
        }

        return len;
    }
    
    private int _disposed;

    protected override void Dispose(bool disposing)
    {
        // Make disposal idempotent (important with pooling / multiple call sites).
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            base.Dispose(disposing);
            return;
        }
        
        _inner.Dispose();
        base.Dispose(disposing);
    }
    
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }
}