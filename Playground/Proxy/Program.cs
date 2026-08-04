using System.Text;
using ioxide;
using ioxide.http11;
using Playground.Shared;
using Playground.Shared.Http;

// proxy - every inbound request is forwarded to an upstream origin through the ring-native HTTP
// client, and the upstream's status and body are relayed back. Both hops - the inbound connection
// and the outbound call - run on this reactor's ring and resume inline, so a proxied request never
// leaves the thread it arrived on.
//
//   # terminal 1: an origin to forward to
//   PLAYGROUND_PORT=8081 dotnet run -c Release --project Playground/Raw
//   # terminal 2: the proxy
//   dotnet run -c Release --project Playground/Proxy

var upstream = new HttpClientOptions
{
    Host = Env.Str("PLAYGROUND_UPSTREAM_HOST", "127.0.0.1"),
    Port = Env.Port("PLAYGROUND_UPSTREAM_PORT", 8081),
    PoolSize = Env.Int("PLAYGROUND_UPSTREAM_POOL", 8),
};

return PlaygroundHost.Run(new PlaygroundSample
{
    Name = "proxy",
    Summary = $"forwards to {upstream.Host}:{upstream.Port} via ioxide.http11",
    Start = reactor => HttpClientPool.Start(reactor, upstream),
    Tcp = (reactor, conn) => ConnectionLoop.ServeAsync(conn, new ProxyResponder(reactor.GetService<HttpClientPool>())),
});

internal readonly struct ProxyResponder(HttpClientPool upstream) : ITcpResponder
{
    public async ValueTask RespondAsync(TcpConnection conn, RecvSnapshot snapshot)
    {
        string path = RequestParser.ReadPath(conn, snapshot);

        try
        {
            using HttpClientResponse response = await upstream.GetAsync(path);
            conn.Write(Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {response.Status} X\r\nContent-Length: {response.Body.Length}\r\n\r\n"));
            conn.Write(response.Body.Span);
        }
        catch (HttpClientException e)
        {
            // A dead origin surfaces here rather than hanging: the pool bounds the whole acquire,
            // so the caller gets an exception and the client gets a 502.
            byte[] message = Encoding.ASCII.GetBytes($"upstream: {e.Message}");
            conn.Write(Encoding.ASCII.GetBytes(
                $"HTTP/1.1 502 Bad Gateway\r\nContent-Length: {message.Length}\r\n\r\n"));
            conn.Write(message);
        }

        await conn.FlushAsync();
    }
}
