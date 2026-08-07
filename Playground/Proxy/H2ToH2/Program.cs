using System.Text;
using ioxide;
using ioxide.httpclient;
using ioxide.nghttp2;
using Playground.Shared;

// ─────────────────────────────────────────────────────────────────────────────────────────────
//  proxy h2->h2 - HTTP/2 on both sides. The narrowest proxy in this folder: n client streams on
//  one inbound connection fan out to n streams on ONE outbound connection, so the whole thing
//  runs on two sockets per reactor no matter how many requests are in flight.
//
//  Two different nghttp2 sessions are involved and they share nothing. The inbound one decodes
//  HPACK against the client's dynamic table; the outbound one re-encodes against the origin's.
//  Header state is per-connection in HTTP/2 and cannot be forwarded - a proxy always re-encodes,
//  which is why "just splice the frames" is not a shortcut that exists.
//
//      # the h2c upstream, moved off 8080 so the proxy can have it
//      PLAYGROUND_PORT=8081 dotnet run -c Release --project Playground/Http2/Nghttp2
//      dotnet run -c Release --project Playground/Proxy/H2ToH2
//      curl --http2-prior-knowledge http://127.0.0.1:8080/anything
//
//  Needs: ioxide.nghttp2, ioxide.httpclient
// ─────────────────────────────────────────────────────────────────────────────────────────────

var config = new ServerConfig
{
    ReactorCount = Env.Int("PLAYGROUND_REACTORS", Environment.ProcessorCount),
    Tcp = new TcpOptions
    {
        Port = Env.Port("PLAYGROUND_PORT", 8080),
    },
};

var upstream = new Http2ClientOptions
{
    Host = Env.Str("PLAYGROUND_UPSTREAM_HOST", "127.0.0.1"),   // IPv4 literal: DNS would block the reactor
    Port = Env.Port("PLAYGROUND_UPSTREAM_PORT", 8081),
    PoolSize = Env.Int("PLAYGROUND_UPSTREAM_POOL", 1),         // h2 multiplexes: one connection carries all
};

var threads = new Thread[config.ReactorCount];

for (int i = 0; i < threads.Length; i++)
{
    var reactor = new Reactor(i, config);

    reactor.OnStart = r => Http2ClientPool.Start(r, upstream);

    reactor.TcpHandle = async (r, conn) =>
    {
        Http2ClientPool client = r.GetService<Http2ClientPool>();

        try
        {
            await new Nghttp2Connection(conn).RunBufferedAsync(async request =>
            {
                try
                {
                    // Both hops are h2 streams on this reactor's ring, and this await resumes
                    // inline - the request never leaves the thread it arrived on.
                    using HttpClientResponse response = await client.SendAsync(new HttpClientRequest(
                        request.Method, request.Path) { Body = request.Body });

                    // Copy before Dispose: the response arena is freed then, and nghttp2 copies
                    // the h2 response only AFTER this handler returns.
                    var proxied = new Nghttp2Response
                    {
                        Status = response.Status,
                        Body = response.Body.ToArray(),
                    };
                    if (response.TryGetHeader("content-type"u8, out ReadOnlyMemory<byte> contentType))
                    {
                        proxied.Headers.Add("content-type"u8.ToArray(), contentType.ToArray());
                    }
                    return proxied;
                }
                catch (Exception e)
                {
                    return new Nghttp2Response
                    {
                        Status = 502,
                        Body = Encoding.ASCII.GetBytes($"upstream failed: {e.Message}\n"),
                    };
                }
            });
        }
        finally
        {
            conn.DecRef();
        }
    };

    threads[i] = new Thread(reactor.Run) { Name = $"reactor-{i}" };
    threads[i].Start();
}

Console.WriteLine($"[proxy h2->h2] {config.ReactorCount} reactors, h2c on :{config.Tcp!.Port} "
                + $"-> h2c {upstream.Host}:{upstream.Port}");

foreach (Thread thread in threads)
{
    thread.Join();
}
