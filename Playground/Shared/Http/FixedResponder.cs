using ioxide;

namespace Playground.Shared.Http;

/// <summary>
/// Drain the recv, write one pre-encoded response, flush. The raw sample IS this, and the HTTP/3
/// samples use it for the TCP port they still listen on, so it lives here rather than in any one of
/// them.
/// </summary>
public readonly struct FixedResponder(byte[] response) : ITcpResponder
{
    public ValueTask RespondAsync(TcpConnection conn, RecvSnapshot snapshot)
    {
        RequestParser.Drain(conn, snapshot);
        conn.Write(response);
        return conn.FlushAsync();
    }
}
