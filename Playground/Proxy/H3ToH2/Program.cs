using ioxide;
using ioxide.httpclient;
using ioxide.nghttp3;
using ioxide.ngtcp2;
using Playground.Shared;

// ─────────────────────────────────────────────────────────────────────────────────────────────
//  proxy h3->h2 - an HTTP/3 front door for an HTTP/2 (h2c) upstream. The shape of Proxy.H3ToH1
//  with the upstream pool swapped: requests multiplex onto a single HTTP/2 connection instead of
//  taking keep-alive h1 connections, so concurrent h3 requests share one upstream socket. Both
//  hops still live on one reactor thread, and the frontend is QUIC-only (Tcp = null).
//
//  h2c is cleartext HTTP/2 with prior knowledge - the upstream must already speak it, e.g.:
//
//      docker run --rm -p 8081:8081 nginx ...        # nginx.conf: listen 8081; http2 on;
//      dotnet run -c Release --project Playground/Proxy/H3ToH2
//      curl --http3-only -ks https://127.0.0.1:8443/index.html
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

    reactor.QuicHandle = (r, conn) =>
    {
        Http2ClientPool client = r.GetService<Http2ClientPool>();

        return new Nghttp3Connection(conn).RunBufferedAsync(async request =>
        {
            try
            {
                // Same request type as the h1 client - the protocols share their message shapes,
                // so swapping the upstream is swapping the pool.
                using HttpClientResponse response = await client.SendAsync(new HttpClientRequest(
                    request.Method, request.Path) { Body = request.Body });

                // Copy before Dispose: the arena is freed then, and nghttp3 copies the h3
                // response only after this handler returns.
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

Console.WriteLine($"[proxy h3->h2] {config.ReactorCount} reactors, h3 on udp :{config.Quic!.Port} "
                + $"-> h2c {upstream.Host}:{upstream.Port}");

foreach (Thread thread in threads)
{
    thread.Join();
}
