using System.IO.Pipelines;

namespace ioxide;

/// <summary>
/// The stream a dual pipe is bound to, shared between its reader and writer so a stream id the
/// reader discovers (auto-bind on the first delivered item) becomes the writer's send target.
/// -1 until bound.
/// </summary>
internal sealed class QuicStreamBinding
{
    public long StreamId = -1;
}

/// <summary>
/// <see cref="IDuplexPipe"/> over ONE stream of a <see cref="QuicConnection"/> - QUIC's mirror of
/// <see cref="TcpConnectionDualPipe"/>, separate code by design. A PipeReader is a single byte
/// stream, so the pipe binds to a single QUIC stream: pass the id, or let it auto-bind to the
/// first stream the peer sends on (the usual server shape - one bidi stream per connection).
/// Items for any other stream are dropped; multi-stream protocols use the raw
/// ReadAsync/TryGetItem surface (or ioxide.nghttp3) instead. One dual pipe per connection - the reader
/// is the connection queue's sole consumer.
/// </summary>
public sealed class QuicConnectionDualPipe : IDuplexPipe
{
    public PipeReader Input { get; }
    public PipeWriter Output { get; }

    /// <summary>The bound stream id, or -1 while auto-bind is still waiting for the first item.</summary>
    public long StreamId => _binding.StreamId;

    private readonly QuicStreamBinding _binding;

    /// <summary>Auto-bind: the first stream the peer delivers on becomes the pipe's stream.</summary>
    public QuicConnectionDualPipe(QuicConnection connection) : this(connection, -1)
    {
    }

    /// <summary>Bind to a known stream id (e.g. one returned by <see cref="QuicConnection.OpenUniStream"/>).</summary>
    public QuicConnectionDualPipe(QuicConnection connection, long streamId)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _binding = new QuicStreamBinding { StreamId = streamId };
        Input = new QuicConnectionPipeReader(connection, _binding);
        Output = new QuicConnectionPipeWriter(connection, _binding);
    }
}
