using ioxide;
using ioxide.httpclient;
using ioxide.nghttp3;
using ioxide.ngtcp2;
using Playground.Shared;

// ─────────────────────────────────────────────────────────────────────────────────────────────
//  proxy h3->h3 - HTTP/3 on both sides. The frontend serves h3 on :8443, and the upstream calls
//  are h3 too, through Http3ClientPool - so each reactor is a QUIC server and a QUIC client at
//  once. The two roles use two sockets on purpose: the serving port is SO_REUSEPORT across
//  reactors, so upstream replies sent there would hash to reactors that do not own the
//  connection - each reactor's outbound rides its own ephemeral-port socket instead, which makes
//  the reply path deterministic.
//
//  This is the one combination that needed no TLS wiring: QUIC has no cleartext mode, so both
//  hops are TLS 1.3 by construction. There is no TlsService on the way in and no
//  TlsClientContext on the way out because the crypto handshake IS the connection handshake -
//  the certificate the QuicEngine serves, and the ServerName the pool verifies, are the whole
//  configuration. Compare the h1 and h2 frontends, where TLS is a layer you add.
//
//      PLAYGROUND_QUIC_PORT=8444 PLAYGROUND_PORT=8090 dotnet run -c Release --project Playground/Http3/Nghttp3
//      dotnet run -c Release --project Playground/Proxy/H3ToH3
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
    Tcp            = null,       // h3 in, h3 out: nothing here speaks TCP at all
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

var upstream = new Http3ClientOptions
{
    Host             = Env.Str("PLAYGROUND_UPSTREAM_HOST", "127.0.0.1"),  // IPv4 literal: DNS would block the reactor
    Port             = Env.Port("PLAYGROUND_UPSTREAM_PORT", 8444),        // the upstream's QUIC (UDP) port
    ServerName       = Env.Str("PLAYGROUND_UPSTREAM_SNI", "localhost"),   // SNI / :authority, checked against the cert
    PoolSize         = Env.Int("PLAYGROUND_UPSTREAM_POOL", 1),            // h3 multiplexes: one connection carries all
    AcquireTimeoutMs = 10_000,                                            // how long a request waits (handshake included)
    MaxResponseBytes = 8 * 1024 * 1024,                                   // per-request ceiling for headers + body
};

var threads = new Thread[config.ReactorCount];

for (int i = 0; i < threads.Length; i++)
{
    var reactor = new Reactor(i, config);

    // The pool's QUIC connections ride this reactor's ring on a client socket of their own.
    reactor.OnStart = r => Http3ClientPool.Start(r, upstream);

    reactor.QuicHandle = (r, conn) =>
    {
        Http3ClientPool client = r.GetService<Http3ClientPool>();

        // The same handler as Proxy.H3ToH1 and Proxy.H3ToH2 - only the pool differs. The clients
        // share their message types, so the upstream protocol is a pool choice, not a rewrite.
        return new Nghttp3Connection(conn).RunBufferedAsync(async request =>
        {
            try
            {
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

Console.WriteLine($"[proxy h3->h3] {config.ReactorCount} reactors, h3 on udp :{config.Quic!.Port} "
                + $"-> h3 {upstream.Host}:{upstream.Port} (per-reactor client socket)");

foreach (Thread thread in threads)
{
    thread.Join();
}
