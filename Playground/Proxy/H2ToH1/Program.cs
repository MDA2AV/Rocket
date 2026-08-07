using System.Text;
using ioxide;
using ioxide.httpclient;
using ioxide.nghttp2;
using Playground.Shared;

// ─────────────────────────────────────────────────────────────────────────────────────────────
//  proxy h2->h1 - an HTTP/2 front door for an HTTP/1.1 upstream. This is the classic edge shape:
//  clients get multiplexing and header compression, the origin keeps speaking the protocol it
//  already speaks.
//
//  Note the asymmetry it creates. One h2 connection can have a hundred streams in flight, and
//  each one needs its own h1 upstream connection for the duration - h1 has no multiplexing to
//  borrow. So the pool sizes for concurrency here, unlike every h2/h3 upstream in this folder.
//  Run out and the request queues behind a waiter rather than the pool opening unbounded, with
//  the whole acquire bounded by HttpClientOptions.AcquireTimeoutMs - a saturated origin surfaces
//  as a 502 on that stream, not as an fd leak.
//
//      PLAYGROUND_PORT=8081 dotnet run -c Release --project Playground/Tcp/Raw
//      dotnet run -c Release --project Playground/Proxy/H2ToH1
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

var upstream = new HttpClientOptions
{
    Host = Env.Str("PLAYGROUND_UPSTREAM_HOST", "127.0.0.1"),   // IPv4 literal: DNS would block the reactor
    Port = Env.Port("PLAYGROUND_UPSTREAM_PORT", 8081),
    PoolSize = Env.Int("PLAYGROUND_UPSTREAM_POOL", 32),        // h1 has no multiplexing: size for concurrency
};

var threads = new Thread[config.ReactorCount];

for (int i = 0; i < threads.Length; i++)
{
    var reactor = new Reactor(i, config);

    // The pool's connections belong to THIS reactor's ring - one pool per reactor, no locks.
    reactor.OnStart = r => HttpClientPool.Start(r, upstream);

    reactor.TcpHandle = async (r, conn) =>
    {
        HttpClientPool client = r.GetService<HttpClientPool>();

        try
        {
            // Buffered + async: each stream dispatches with its body assembled, and the handler
            // may await - the upstream round trip resumes inline on this reactor. Concurrent
            // streams interleave here, which is exactly why the h1 pool has to be deep.
            await new Nghttp2Connection(conn).RunBufferedAsync(async request =>
            {
                try
                {
                    // Method, path and body forward as the bytes they already are: h2 decoded
                    // them out of HPACK, and the h1 client writes them back out as a request line.
                    using HttpClientResponse response = await client.SendAsync(new HttpClientRequest(
                        request.Method, request.Path) { Body = request.Body });

                    // Copy before Dispose: the response arena is freed then, and nghttp2 copies
                    // the h2 response only AFTER this handler returns. A real proxy would also
                    // drop hop-by-hop headers - Connection, Keep-Alive, Transfer-Encoding are all
                    // illegal in h2 and would be a protocol error to forward.
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
                    // Upstream down is a gateway error on this stream, not a dead h2 connection:
                    // every other stream on it keeps working.
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

Console.WriteLine($"[proxy h2->h1] {config.ReactorCount} reactors, h2c on :{config.Tcp!.Port} "
                + $"-> http/1.1 {upstream.Host}:{upstream.Port}, {upstream.PoolSize} connections each");

foreach (Thread thread in threads)
{
    thread.Join();
}
