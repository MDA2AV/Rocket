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

using var engine = new QuicEngine(certPath, keyPath,
    cidLength: 8,                       // CID bytes this endpoint mints (1..20)
    alpn: ["h3"],                       // the only protocol offered (else no_application_protocol)
    maxSendRetentionBytes: 16L << 20);  // per-connection send-retention high-water (default 16 MiB)

var config = new ServerConfig
{
    ReactorCount   = Env.Int("PLAYGROUND_REACTORS", Environment.ProcessorCount),
    RingEntries    = 8192,       // SQ/CQ depth per ring
    DualStack      = false,      // true = one IPv6 socket also accepts IPv4-mapped
    RecvBufferSize = 32 * 1024,  // bytes per shared recv buffer
    RecvSlots      = 4096,       // shared recv buffer-ring depth
    Incremental    = null,       // per-connection recv rings (6.12+) - see Tcp/Incremental
    Tcp            = null,       // the proxy serves h3 only; its TCP sockets are all outbound
    Udp = new UdpOptions
    {
        RecvSlots = Env.Int("PLAYGROUND_UDP_SLOTS", 16),  // multishot recv slots per reactor (datagrams in flight)
        Gro       = true,                                 // UDP_GRO: coalesce datagrams into one recv (fewer syscalls)
    },
    Quic = new QuicOptions
    {
        Port              = Env.Port("PLAYGROUND_QUIC_PORT", 8443),  // h3 over UDP - the QUIC listener
        LocalCidLength    = 8,                                       // CID bytes this endpoint mints (must match the engine)
        IdleTimeoutMs     = 60_000,                                  // transport idle backstop; 0 disables sweep eviction
        ConnectionFactory = engine.CreateFactory(),                  // adopts new connections into the engine
    },
};

string upstreamHost = Env.Str("PLAYGROUND_UPSTREAM_HOST", "127.0.0.1");   // IPv4 literal: DNS would block the reactor
ushort upstreamPort = Env.Port("PLAYGROUND_UPSTREAM_PORT", 8444);
int upstreamPool = Env.Int("PLAYGROUND_UPSTREAM_POOL", 8);                // keep-alive connections per reactor

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
        HttpClientPool.Start(r, new HttpClientOptions
        {
            Host              = upstreamHost,     // origin address (IPv4 literal; DNS would block the reactor)
            Port              = upstreamPort,     // origin port
            PoolSize          = upstreamPool,     // connections kept open; round-robin, one request each
            MaxResponseBytes  = 8 * 1024 * 1024,  // per-request ceiling for headers + body
            SendBufferSize    = 16 * 1024,        // per-connection send buffer
            ReceiveBufferSize = 16 * 1024,        // per-connection recv buffer (grows to MaxResponseBytes)
            AcquireTimeoutMs  = 10_000,           // how long a request waits for a free connection
            Tls = TlsClientContext.Create(new TlsClientOptions
            {
                ServerName         = upstreamName,                          // sent as SNI, checked against the cert
                AlpnProtocols      = ["http/1.1"],                          // ALPN offer, most preferred first
                VerifyCertificate  = !upstreamInsecure,                     // off = encrypted but UNAUTHENTICATED
                CaFile             = upstreamInsecure ? null : upstreamCa,  // PEM trust anchors; null = system store
                MinimumVersion     = OpenSslVersions.Tls12,                 // lowest TLS accepted; Tls13 = 1.3 only
                HandshakeTimeoutMs = 10_000,                                // handshake deadline
            }),
        });
    };

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
                + $"-> https://{upstreamName} ({upstreamHost}:{upstreamPort}), "
                + $"verify={(upstreamInsecure ? "OFF" : upstreamCa ?? "system store")}");

foreach (Thread thread in threads)
{
    thread.Join();
}
