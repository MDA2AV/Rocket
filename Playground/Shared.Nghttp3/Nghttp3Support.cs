using ioxide;
using ioxide.nghttp3;

namespace Playground.Shared.Nghttp3;

/// <summary>
/// What the two nghttp3 samples (streamed and buffered dispatch) share: the QPACK settings, the
/// live-connection registry that makes a graceful GOAWAY possible, and the reusable header/response
/// constants.
/// </summary>
public static class Nghttp3Support
{
    // Field names/values reused across responses: static byte literals, so a response that uses only
    // these allocates nothing beyond the response object itself.
    public static readonly byte[] ContentType   = "content-type"u8.ToArray();
    public static readonly byte[] TextPlain     = "text/plain"u8.ToArray();
    public static readonly byte[] SetCookie     = "set-cookie"u8.ToArray();
    public static readonly byte[] ServerName    = "server"u8.ToArray();
    public static readonly byte[] ServerValue   = "ioxide"u8.ToArray();
    public static readonly byte[] SessionCookie = "session=demo; Path=/; HttpOnly; SameSite=Lax"u8.ToArray();

    /// <summary>
    /// The allocation-free response pattern: build it ONCE and reuse the instance for every request.
    /// Legal because the h3 layer copies status, headers and body into nghttp3 synchronously at
    /// submit and never retains the object - so a static response costs zero allocations per request,
    /// unlike <c>Nghttp3Response.Text($"...")</c> which encodes a fresh string every time. This is
    /// what a hot path should look like.
    /// </summary>
    public static readonly Nghttp3Response PlaintextResponse = BuildPlaintextResponse();

    private static Nghttp3Response BuildPlaintextResponse()
    {
        var response = new Nghttp3Response { Body = "Hello, World!"u8.ToArray() };
        response.Headers.Add(ContentType, TextPlain);
        response.Headers.Add(ServerName, ServerValue);
        return response;
    }

    /// <summary>
    /// QPACK settings from <c>PLAYGROUND_QPACK_CAP</c>: a non-zero capacity advertises a decode-side
    /// dynamic table (blocked streams 100); 0 is static-only, nghttp3's default.
    /// </summary>
    public static Nghttp3Options OptionsFromEnvironment()
    {
        long capacity = Env.Long("PLAYGROUND_QPACK_CAP", 0);
        return new Nghttp3Options
        {
            QpackDynamicTableCapacity = capacity,
            QpackBlockedStreams = capacity > 0 ? 100 : 0,
        };
    }

    // Live nghttp3 connections, so a SIGTERM can GOAWAY them all. Each reactor only ever adds its
    // own, but a plain lock keeps the signal handler - which runs off-reactor - honest.
    private static readonly List<(Reactor Reactor, Nghttp3Connection Connection)> Live = [];

    /// <summary>Register a connection so <see cref="ShutdownAll"/> can drain it.</summary>
    public static Nghttp3Connection Track(Reactor reactor, Nghttp3Connection connection)
    {
        lock (Live)
        {
            Live.Add((reactor, connection));
        }
        return connection;
    }

    /// <summary>
    /// Graceful drain: GOAWAY every live connection. Called from the SIGTERM handler, i.e. OFF the
    /// reactor threads - so each Shutdown is marshalled onto its owning reactor, which is where
    /// nghttp3 and the send path must be touched. Each connection finishes its in-flight requests,
    /// then closes itself.
    /// </summary>
    public static void ShutdownAll()
    {
        lock (Live)
        {
            foreach ((Reactor reactor, Nghttp3Connection connection) in Live)
            {
                reactor.ScheduleOnReactor(static state => ((Nghttp3Connection)state!).Shutdown(), connection);
            }
            Live.Clear();
        }
    }
}
