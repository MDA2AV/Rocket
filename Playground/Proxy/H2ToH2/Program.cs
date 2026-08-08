using System.Text;
using ioxide;
using ioxide.httpclient;
using ioxide.nghttp2;
using ioxide.tls;
using Playground.Shared;

// ─────────────────────────────────────────────────────────────────────────────────────────────
//  proxy h2->h2 - HTTP/2 on both sides, TLS on both hops. The narrowest proxy in this folder: n
//  client streams on one inbound connection fan out to n streams on ONE outbound connection, so
//  the whole thing runs on two sockets per reactor no matter how many requests are in flight.
//
//  ALPN carries h2 in both directions - offered by the server on the way in, offered by the
//  client on the way out. Neither side ever assumes it.
//
//  Two different nghttp2 sessions are involved and they share nothing. The inbound one decodes
//  HPACK against the client's dynamic table; the outbound one re-encodes against the origin's.
//  Header state is per-connection in HTTP/2 and cannot be forwarded - a proxy always re-encodes,
//  which is why "just splice the frames" is not a shortcut that exists. TLS makes that doubly
//  true: the two connections do not even share a key.
//
//      # an h2-over-TLS origin to forward to
//      PLAYGROUND_PORT=8444 dotnet run -c Release --project Playground/Http2/Tls
//      dotnet run -c Release --project Playground/Proxy/H2ToH2
//      curl -k --http2 https://127.0.0.1:8443/
//
//  Needs: ioxide, ioxide.nghttp2, ioxide.httpclient
// ─────────────────────────────────────────────────────────────────────────────────────────────

(string certPath, string keyPath) = QuicCert.Ensure(
    Env.StrOrNull("PLAYGROUND_TLS_CERT"),
    Env.StrOrNull("PLAYGROUND_TLS_KEY"));

var config = new ServerConfig
{
    ReactorCount   = Env.Int("PLAYGROUND_REACTORS", Environment.ProcessorCount),
    RingEntries    = 8192,       // SQ/CQ depth per ring
    DualStack      = false,      // true = one IPv6 socket also accepts IPv4-mapped
    RecvBufferSize = 32 * 1024,  // bytes per shared recv buffer
    RecvSlots      = 4096,       // shared recv buffer-ring depth
    Incremental    = null,       // per-connection recv rings (6.12+) - see Tcp/Incremental
    Udp            = null,       // no raw UDP sockets (TCP-only frontend)
    Quic           = null,       // no QUIC listener; the frontend is TLS-over-TCP
    Tcp = new TcpOptions
    {
        Port             = Env.Port("PLAYGROUND_PORT", 8443),
        ExtraPorts       = [],                                 // extra listener ports (one handler, several doors)
        ListenBacklog    = 1024,                               // accept-queue depth per SO_REUSEPORT listener
        WriteSlabSize    = 16 * 1024,                          // per-connection write buffer before overflow kicks in
        PoolMax          = 1024,                               // pooled connection objects kept per reactor
        WriteOverflow    = WriteOverflowStrategy.Grow,         // Grow = realloc one slab; Segmented = chain + vectored SENDMSG
        ZeroCopySend     = false,                              // SEND_ZC: kernel copies less, wins on large writes
        RecvQueueEntries = 64,                                 // per-connection recv completion queue depth
    },
};

string upstreamHost = Env.Str("PLAYGROUND_UPSTREAM_HOST", "127.0.0.1");   // IPv4 literal: DNS would block the reactor
ushort upstreamPort = Env.Port("PLAYGROUND_UPSTREAM_PORT", 8444);
string upstreamName = Env.Str("PLAYGROUND_UPSTREAM_SNI", "localhost");    // sent as SNI, checked against the cert
int upstreamPool = Env.Int("PLAYGROUND_UPSTREAM_POOL", 1);                // h2 multiplexes: one connection carries all

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
        // Inbound: terminate TLS and offer only h2, because that is all this frontend serves. A
        // client that cannot do h2 fails ALPN rather than silently getting something else.
        TlsService.Start(r, new TlsOptions
        {
            CertificatePath = certPath,  // PEM cert chain file (set exactly one of Path/Pem)
            CertificatePem  = null,      // in-memory PEM alternative to CertificatePath
            KeyPath         = keyPath,   // PEM private key file (set exactly one of Path/Pem)
            KeyPem          = null,      // in-memory PEM alternative to KeyPath
            Alpn            = ["h2"],    // protocols offered, most preferred first
            KernelTx        = false,     // kTLS encrypt (off = OpenSSL both ways)
            KernelRx        = false,     // kTLS decrypt; requires KernelTx, experimental
        });

        // Outbound: one context per reactor, shared by that reactor's pool - it holds no
        // per-connection state, so every connection opened from it gets its own SSL.
        Http2ClientPool.Start(r, new Http2ClientOptions
        {
            Host             = upstreamHost,     // origin address (IPv4 literal; DNS would block the reactor)
            Port             = upstreamPort,     // origin port
            PoolSize         = upstreamPool,     // h2 multiplexes: one connection carries many streams
            AcquireTimeoutMs = 10_000,           // how long a request waits for a usable connection
            MaxResponseBytes = 8 * 1024 * 1024,  // per-request ceiling for headers + body
            Tls = TlsClientContext.Create(new TlsClientOptions
            {
                ServerName         = upstreamName,                          // sent as SNI, checked against the cert
                AlpnProtocols      = ["h2"],                                // h2 over TLS is chosen by ALPN, never assumed
                VerifyCertificate  = !upstreamInsecure,                     // off = encrypted but UNAUTHENTICATED
                CaFile             = upstreamInsecure ? null : upstreamCa,  // PEM trust anchors; null = system store
                MinimumVersion     = OpenSslVersions.Tls12,                 // lowest TLS accepted; Tls13 = 1.3 only
                HandshakeTimeoutMs = 10_000,                                // handshake deadline
            }),
        });
    };

    reactor.TcpHandle = async (r, conn) =>
    {
        Http2ClientPool client = r.GetService<Http2ClientPool>();
        TlsSession? tls = null;

        try
        {
            tls = await r.GetService<TlsService>().AcceptAsync(conn);

            // The decrypt pump lives in the pipe, so everything below is the cleartext sample -
            // and the pipe resumes inline on this reactor, so the upstream call below does too.
            await using var pipe = new TlsConnectionDualPipe(conn, tls, ownsSession: false);

            // Buffered + async: each stream dispatches with its body assembled, and the handler
            // may await - the upstream round trip resumes inline on this reactor.
            await new Nghttp2Connection(pipe).RunBufferedAsync(async request =>
            {
                try
                {
                    // Both hops are h2 streams on this reactor's ring, and this await resumes
                    // inline - the request never leaves the thread it arrived on.
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
                    // every other stream on it keeps working. A refused certificate arrives the
                    // same way - the handshake is part of opening the upstream connection.
                    return new Nghttp2Response
                    {
                        Status = 502,
                        Body = Encoding.ASCII.GetBytes($"upstream failed: {e.Message}\n"),
                    };
                }
            });
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[proxy h2->h2] connection failed: {e.Message}");
        }
        finally
        {
            tls?.Dispose();
            conn.DecRef();
        }
    };

    threads[i] = new Thread(reactor.Run) { Name = $"reactor-{i}" };
    threads[i].Start();
}

Console.WriteLine($"[proxy h2->h2] {config.ReactorCount} reactors, h2 over TLS on :{config.Tcp!.Port} "
                + $"-> h2 https://{upstreamName} ({upstreamHost}:{upstreamPort}), "
                + $"{upstreamPool} connection(s) each, "
                + $"verify={(upstreamInsecure ? "OFF" : upstreamCa ?? "system store")}");

foreach (Thread thread in threads)
{
    thread.Join();
}
