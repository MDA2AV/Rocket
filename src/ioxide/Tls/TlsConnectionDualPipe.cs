using System.Buffers;
using System.IO.Pipelines;
using ioxide.utils;

namespace ioxide.tls;

/// <summary>
/// A duplex pipe over a TLS connection, for the frameworks that serve from <see cref="IDuplexPipe"/>
/// rather than from the raw ring.
///
/// The two halves are not symmetric, because ioxide's TLS is not:
///
///   OUTBOUND - nothing to do. kTLS TX was enabled during the handshake, so the kernel produces the
///              records; this delegates straight to <see cref="TcpConnectionDualPipe"/> and the
///              writes plaintext exactly as it would without TLS.
///   INBOUND  - a pump. Plaintext does not exist in ring memory, so the zero-copy reader has nothing
///              to hand out: the pump reads ciphertext slices, decrypts them into a Pipe this class
///              owns, and the caller reads that.
///
/// Every consumer that serves TLS over pipes has had to write that pump itself, and the two in the
/// wild both drop faults and both had to rediscover the handshake prologue below. Having it once
/// here is the point.
/// </summary>
/// <remarks>
/// Reactor thread only, like everything else that touches a connection.
/// </remarks>
public sealed class TlsConnectionDualPipe : IDuplexPipe, IAsyncDisposable
{
    private readonly TcpConnection _connection;
    private readonly TlsSession _tls;
    private readonly bool _ownsSession;

    private readonly Pipe _inbound;
    private readonly TcpConnectionDualPipe _outbound;   // only its writer is used
    private readonly Task _pump;

    /// <summary>
    /// Wrap a connection whose TLS handshake has already completed (see
    /// <see cref="TlsService.AcceptAsync"/>).
    /// </summary>
    /// <param name="connection">The accepted connection, post-handshake.</param>
    /// <param name="session">The session that handshake produced.</param>
    /// <param name="options">
    /// Buffering for the inbound pipe. Its pause threshold is what applies backpressure: the pump
    /// awaits each flush, so a slow reader stops it pulling from the ring rather than letting
    /// plaintext pile up without bound.
    /// </param>
    /// <param name="ownsSession">
    /// When true (the default) disposing this also disposes <paramref name="session"/>, which is
    /// what sends the closing close_notify.
    /// </param>
    public TlsConnectionDualPipe(TcpConnection connection, TlsSession session,
        PipeOptions? options = null, bool ownsSession = true)
    {
        _connection = connection;
        _tls = session;
        _ownsSession = ownsSession;

        _inbound = new Pipe(options ?? new PipeOptions(useSynchronizationContext: false));
        _outbound = new TcpConnectionDualPipe(connection);

        _pump = PumpInboundAsync();
    }

    /// <summary>Decrypted request bytes.</summary>
    public PipeReader Input => _inbound.Reader;

    /// <summary>Response bytes, written as PLAINTEXT - the kernel encrypts them.</summary>
    public PipeWriter Output => _outbound.Output;

    private async Task PumpInboundAsync()
    {
        PipeWriter writer = _inbound.Writer;
        Exception? failure = null;

        try
        {
            if (!await WriteHandshakePlaintextAsync(writer))
            {
                return;
            }

            while (true)
            {
                RecvSnapshot snapshot = await _connection.ReadAsync();
                int produced = DecryptAvailable(snapshot, writer);
                _connection.ResetRead();

                if (produced > 0)
                {
                    FlushResult flush = await writer.FlushAsync();
                    if (flush.IsCompleted || flush.IsCanceled)
                    {
                        return;   // the reader is gone
                    }
                }

                // close_notify is a clean end of stream; a closed snapshot without one is the peer
                // vanishing. Both stop the pump, and the difference is left to the caller, which
                // can still read TlsSession.Closed.
                if (_tls.Closed || snapshot.IsClosed)
                {
                    return;
                }
            }
        }
        catch (Exception e)
        {
            // Deliberately kept, not swallowed. Completing the pipe cleanly on a TLS fault would
            // make a bad MAC or a truncated stream indistinguishable from the peer hanging up
            // politely - which is exactly what a truncation attack wants it to look like.
            failure = e;
        }
        finally
        {
            await writer.CompleteAsync(failure);
        }
    }

    /// <summary>
    /// The client's first request usually rides in with its Finished flight, so the handshake has
    /// already decrypted it. Miss this and the first request is silently dropped, then the pump
    /// parks waiting for bytes that arrived before it started.
    /// </summary>
    private async ValueTask<bool> WriteHandshakePlaintextAsync(PipeWriter writer)
    {
        if (!WriteInitialPlaintext(writer))
        {
            return true;   // nothing rode in with the handshake
        }

        FlushResult flush = await writer.FlushAsync();
        return !flush.IsCompleted && !flush.IsCanceled;
    }

    private bool WriteInitialPlaintext(PipeWriter writer)
    {
        ReadOnlySpan<byte> initial = _tls.DrainPlaintext();
        if (initial.IsEmpty)
        {
            return false;
        }

        writer.Write(initial);
        return true;
    }

    // Decrypt every buffer this snapshot carries, straight into the pipe. Each one is returned to
    // the ring whatever happens: a decrypt that throws must not strand the kernel's buffer.
    private unsafe int DecryptAvailable(in RecvSnapshot snapshot, PipeWriter writer)
    {
        int produced = 0;

        while (_connection.TryGetItem(snapshot, out SpscRecvRing.Item item))
        {
            try
            {
                if (item.HasBuffer)
                {
                    produced += _tls.DecryptInto(item.Ptr, item.Len, writer);
                }
            }
            finally
            {
                if (item.HasBuffer)
                {
                    _connection.ReturnBuffer(in item);
                }
            }
        }

        return produced;
    }

    public async ValueTask DisposeAsync()
    {
        // Cancelling the reader unblocks a pump parked in FlushAsync; the connection's own teardown
        // is what releases one parked in ReadAsync.
        _inbound.Reader.CancelPendingRead();

        try
        {
            await _pump;
        }
        catch
        {
            // The pump reports faults through the pipe, so anything here is teardown noise.
        }

        if (_ownsSession)
        {
            _tls.Dispose();   // sends close_notify over kTLS when the peer has not already closed
        }
    }
}
