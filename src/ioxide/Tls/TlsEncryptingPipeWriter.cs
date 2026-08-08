using System.Buffers;
using System.IO.Pipelines;

namespace ioxide.tls;

/// <summary>
/// The write half when the kernel is <b>not</b> encrypting: plaintext is staged here, OpenSSL turns
/// it into records on flush, and the records go into the connection's slab.
///
/// The counterpart is <see cref="TcpConnectionPipeWriter"/>, which is what a kTLS-TX connection
/// uses - there, plaintext goes straight into the slab and the kernel makes the records on send.
/// This class is the entire difference between the two worlds on the write side, and the reason
/// <see cref="TlsConnectionDualPipe"/> can be a composer rather than a hierarchy.
/// </summary>
/// <remarks>
/// The staging buffer is the copy kTLS avoids. SSL_write needs the plaintext contiguously, so it
/// cannot be handed the slab directly - but BIO_read afterwards writes the records straight into
/// the slab, so there is exactly one extra pass over the bytes, not two.
///
/// Reactor thread only.
/// </remarks>
public sealed class TlsEncryptingPipeWriter : PipeWriter
{
    private readonly TcpConnection _conn;
    private readonly TlsSession _tls;

    private byte[] _staging;
    private int _staged;
    private bool _completed;
    private bool _cancelRequested;

    public TlsEncryptingPipeWriter(TcpConnection connection, TlsSession session, int initialCapacity = 16 * 1024)
    {
        _conn = connection ?? throw new ArgumentNullException(nameof(connection));
        _tls = session ?? throw new ArgumentNullException(nameof(session));
        _staging = ArrayPool<byte>.Shared.Rent(Math.Max(initialCapacity, 4 * 1024));
    }

    public override bool CanGetUnflushedBytes => true;

    public override long UnflushedBytes => _staged;

    public override Memory<byte> GetMemory(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return _staging.AsMemory(_staged);
    }

    public override Span<byte> GetSpan(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return _staging.AsSpan(_staged);
    }

    public override void Advance(int bytes) => _staged += bytes;

    public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
    {
        if (_cancelRequested)
        {
            _cancelRequested = false;
            return new ValueTask<FlushResult>(new FlushResult(isCanceled: true, isCompleted: _completed));
        }

        if (_staged > 0)
        {
            // Encrypt into the connection's slab, then let the ordinary send path move it.
            _tls.WriteEncrypted(_conn, _staging.AsSpan(0, _staged));
            _staged = 0;
        }

        return FlushConnectionAsync();
    }

    private async ValueTask<FlushResult> FlushConnectionAsync()
    {
        await _conn.FlushAsync();
        return new FlushResult(isCanceled: false, isCompleted: _completed);
    }

    public override void CancelPendingFlush() => _cancelRequested = true;

    public override void Complete(Exception? exception = null)
    {
        if (_completed)
        {
            return;
        }
        _completed = true;

        if (_staging.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_staging);
            _staging = [];
        }
        _staged = 0;
    }

    private void Ensure(int sizeHint)
    {
        int want = Math.Max(sizeHint, 1);
        if (_staging.Length - _staged >= want)
        {
            return;
        }

        // A TLS record carries at most 16 KiB of plaintext, but a caller may stage far more than
        // one record before flushing - SSL_write splits it into records itself.
        int size = Math.Max(_staging.Length * 2, _staged + want);
        byte[] grown = ArrayPool<byte>.Shared.Rent(size);
        _staging.AsSpan(0, _staged).CopyTo(grown);
        ArrayPool<byte>.Shared.Return(_staging);
        _staging = grown;
    }
}
