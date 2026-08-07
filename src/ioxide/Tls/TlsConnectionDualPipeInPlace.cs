using System.IO.Pipelines;

namespace ioxide.tls;

/// <summary>
/// <see cref="TlsConnectionDualPipe"/> without the pump: the inbound half decrypts records
/// <b>in place inside the ring buffer</b> and hands that memory straight out, so there is no owned
/// plaintext buffer, no background task and no <see cref="Pipe"/> anywhere in the connection.
///
/// Both classes present the same seam and either can be handed to <c>Nghttp2Connection</c>,
/// Kestrel or GenHTTP unchanged. What differs is what sits behind <see cref="Input"/>:
///
/// <code>
///                        TlsConnectionDualPipe          TlsConnectionDualPipeInPlace
///   plaintext lives in    a Pipe this class owns         the recv buffer it arrived in
///   moving parts          pump Task + Pipe               neither
///   backpressure          the Pipe's pause threshold     the ring itself
///   copies                ring -> BIO, BIO -> Pipe       ring -> BIO, BIO -> ring
/// </code>
///
/// The copy count is the same - the saving is the second buffer, the task and the threshold. And
/// because the ring is the backpressure, a consumer that stops reading stops the kernel filling,
/// which is the same mechanism TCP already has rather than a number someone picked.
///
/// The outbound half is identical in both: kTLS TX means the kernel makes the records, so writes
/// go straight to the connection as plaintext.
/// </summary>
/// <remarks>
/// Reactor thread only. Experimental - <see cref="TlsConnectionDualPipe"/> is the one in use.
/// </remarks>
public sealed class TlsConnectionDualPipeInPlace : IDuplexPipe, IAsyncDisposable
{
    private readonly TlsSession _tls;
    private readonly bool _ownsSession;

    private readonly TlsInPlacePipeReader _inbound;
    private readonly TcpConnectionDualPipe _outbound;   // only its writer is used

    /// <summary>
    /// Wrap a connection whose TLS handshake has already completed (see
    /// <see cref="TlsService.AcceptAsync"/>).
    /// </summary>
    /// <param name="connection">The accepted connection, post-handshake.</param>
    /// <param name="session">The session that handshake produced.</param>
    /// <param name="ownsSession">
    /// When true (the default) disposing this also disposes <paramref name="session"/>, which is
    /// what sends the closing close_notify.
    /// </param>
    public TlsConnectionDualPipeInPlace(TcpConnection connection, TlsSession session,
        bool ownsSession = true)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(session);

        _tls = session;
        _ownsSession = ownsSession;

        _inbound = new TlsInPlacePipeReader(connection, session);
        _outbound = new TcpConnectionDualPipe(connection);
    }

    /// <summary>Decrypted request bytes, pointing into the ring buffers they arrived in.</summary>
    public PipeReader Input => _inbound;

    /// <summary>Response bytes, written as PLAINTEXT - the kernel encrypts them.</summary>
    public PipeWriter Output => _outbound.Output;

    public ValueTask DisposeAsync()
    {
        // No pump to await: completing the reader is what hands every held buffer back to the ring.
        _inbound.Complete();

        if (_ownsSession)
        {
            _tls.Dispose();   // sends close_notify over kTLS when the peer has not already closed
        }

        return ValueTask.CompletedTask;
    }
}
