using ioxide;
using ioxide.httpclient;
using ioxide.nghttp3;
using ioxide.ngtcp2;
using Playground.Shared;

// ─────────────────────────────────────────────────────────────────────────────────────────────
//  proxy h3->h1 - an HTTP/3 front door for an HTTP/1.1 upstream. Both hops live on ONE reactor
//  thread: the h3 request arrives on this ring, the upstream call rides the same ring through a
//  keep-alive HttpClientPool, and the await resumes inline - no thread pool between the hops.
//  The frontend is also QUIC-only (Tcp = null): the proxy itself binds no TCP port.
//
//      PLAYGROUND_PORT=8081 dotnet run -c Release --project Playground/Tcp/Raw   # the upstream
//      dotnet run -c Release --project Playground/Proxy/H3ToH1
//      curl --http3-only -ks https://127.0.0.1:8443/anything
//
//  Needs: ioxide.ngtcp2, ioxide.nghttp3, ioxide.httpclient
// ─────────────────────────────────────────────────────────────────────────────────────────────

(string certPath, string keyPath) = QuicCert.Ensure(
    Env.StrOrNull("PLAYGROUND_QUIC_CERT"),
    Env.StrOrNull("PLAYGROUND_QUIC_KEY"));

using var engine = new QuicEngine(certPath, keyPath, cidLength: 8, alpn: ["h3"]);

var config = new ServerConfig
{
    ReactorCount = Env.Int("PLAYGROUND_REACTORS", Environment.ProcessorCount),
    Tcp = null,   // the proxy serves h3 only; its TCP sockets are all outbound
    Udp = new UdpOptions { RecvSlots = Env.Int("PLAYGROUND_UDP_SLOTS", 16) },
    Quic = new QuicOptions
    {
        Port = Env.Port("PLAYGROUND_QUIC_PORT", 8443),
        LocalCidLength = 8,
        ConnectionFactory = engine.CreateFactory(),
    },
};

var upstream = new HttpClientOptions
{
    Host = Env.Str("PLAYGROUND_UPSTREAM_HOST", "127.0.0.1"),   // IPv4 literal: DNS would block the reactor
    Port = Env.Port("PLAYGROUND_UPSTREAM_PORT", 8081),
    PoolSize = Env.Int("PLAYGROUND_UPSTREAM_POOL", 8),         // keep-alive connections per reactor
};

var threads = new Thread[config.ReactorCount];

for (int i = 0; i < threads.Length; i++)
{
    var reactor = new Reactor(i, config);

    // The pool's connections belong to THIS reactor's ring - one pool per reactor, no locks.
    reactor.OnStart = r => HttpClientPool.Start(r, upstream);

    reactor.QuicHandle = (r, conn) =>
    {
        HttpClientPool client = r.GetService<HttpClientPool>();

        // Buffered + async: each request dispatches with its body assembled, and the handler may
        // await - the upstream round trip resumes inline on this reactor.
        return new Nghttp3Connection(conn).RunBufferedAsync(async request =>
        {
            try
            {
                // Method, path and body forward as the bytes they already are. The request's
                // memories stay valid across the await - the handler owns them until it returns.
                using HttpClientResponse response = await client.SendAsync(new HttpClientRequest(
                    request.Method, request.Path) { Body = request.Body });

                // The upstream response is arena-backed and freed at Dispose, but nghttp3 copies
                // the h3 response only AFTER this handler returns - so take a copy now. A real
                // proxy would also filter hop-by-hop headers here.
                var proxied = new Nghttp3Response
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
                // Upstream down is a gateway error, not a dead h3 connection.
                return new Nghttp3Response
                {
                    Status = 502,
                    Body = System.Text.Encoding.ASCII.GetBytes($"upstream failed: {e.Message}\n"),
                };
            }
        });
    };

    threads[i] = new Thread(reactor.Run) { Name = $"reactor-{i}" };
    threads[i].Start();
}

Console.WriteLine($"[proxy h3->h1] {config.ReactorCount} reactors, h3 on udp :{config.Quic!.Port} "
                + $"-> http/1.1 {upstream.Host}:{upstream.Port}");

foreach (Thread thread in threads)
{
    thread.Join();
}
