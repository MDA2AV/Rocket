using ioxide;
using ioxide.httpclient;
using ioxide.nghttp3;
using ioxide.ngtcp2;
using Playground.Shared;

// ─────────────────────────────────────────────────────────────────────────────────────────────
//  proxy h3->h2 - an HTTP/3 front door for an HTTP/2 upstream. The shape of Proxy.H3ToH1
//  with the upstream pool swapped: requests multiplex onto a single HTTP/2 connection instead of
//  taking keep-alive h1 connections, so concurrent h3 requests share one upstream socket. Both
//  hops still live on one reactor thread, and the frontend is QUIC-only (Tcp = null).
//
//  The upstream speaks h2 over TLS - chosen by ALPN, certificate verified (the playground's own
//  self-signed cert is trusted by default). The Http2/Tls sample makes a ready origin:
//
//      PLAYGROUND_PORT=8444 dotnet run -c Release --project Playground/Http2/Tls
//      dotnet run -c Release --project Playground/Proxy/H3ToH2
//      curl --http3-only -ks https://127.0.0.1:8443/
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

string upstreamHost = Env.Str("PLAYGROUND_UPSTREAM_HOST", "127.0.0.1");   // IPv4 literal: DNS would block the reactor
ushort upstreamPort = Env.Port("PLAYGROUND_UPSTREAM_PORT", 8444);
int upstreamPool = Env.Int("PLAYGROUND_UPSTREAM_POOL", 1);                // h2 multiplexes: one connection carries all

string upstreamName = Env.Str("PLAYGROUND_UPSTREAM_SNI", "localhost");    // sent as SNI, checked against the cert

// The playground's origins use a self-signed cert, so trust that file rather than the system
// store. PLAYGROUND_UPSTREAM_CA points at a private CA instead; PLAYGROUND_UPSTREAM_INSECURE=1
// skips verification, which leaves the hop encrypted but UNAUTHENTICATED - anything in the path
// can present its own certificate and rewrite the whole exchange.
string? upstreamCa = Env.StrOrNull("PLAYGROUND_UPSTREAM_CA") ?? certPath;
bool upstreamInsecure = Env.Flag("PLAYGROUND_UPSTREAM_INSECURE");

var threads = new Thread[config.ReactorCount];

for (int i = 0; i < threads.Length; i++)
{
    var reactor = new Reactor(i, config);

    reactor.OnStart = r =>
    {
        // Outbound: one context per reactor, shared by that reactor's pool - it holds no
        // per-connection state, so every connection opened from it gets its own SSL.
        Http2ClientPool.Start(r, new Http2ClientOptions
        {
            Host = upstreamHost,
            Port = upstreamPort,
            PoolSize = upstreamPool,
            Tls = TlsClientContext.Create(new TlsClientOptions
            {
                ServerName = upstreamName,
                AlpnProtocols = ["h2"],       // h2 over TLS is chosen by ALPN, never assumed
                CaFile = upstreamInsecure ? null : upstreamCa,
                VerifyCertificate = !upstreamInsecure,
            }),
        });
    };

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
                + $"-> h2 https://{upstreamName} ({upstreamHost}:{upstreamPort}), "
                + $"verify={(upstreamInsecure ? "OFF" : upstreamCa ?? "system store")}");

foreach (Thread thread in threads)
{
    thread.Join();
}
