using System.Text;
using ioxide;
using ioxide.httpclient;
using ioxide.nghttp2;
using ioxide.tls;
using Playground.Shared;

// ─────────────────────────────────────────────────────────────────────────────────────────────
//  proxy h2->h3 - HTTP/2 in over TCP, HTTP/3 out over QUIC, encrypted end to end. The
//  protocol-translating edge: the client half of a migration where the origin moved to h3 and the
//  clients have not.
//
//  Both hops are encrypted and get there completely differently. INBOUND is TLS on a TCP
//  socket, terminated here - OpenSSL both ways by default. OUTBOUND is QUIC,
//  where TLS is inside the transport and the crypto handshake IS the connection handshake - so
//  the upstream pool takes no TLS options at all, only the ServerName the certificate must match.
//
//  Note also what the config does NOT contain: no ServerConfig.Quic, no UDP ports. Being an
//  HTTP/3 client requires no HTTP/3 server - the first connect opens an ephemeral UDP socket on
//  this reactor's ring and replies route back by connection ID.
//
//      dotnet run -c Release --project Playground/Http3/Nghttp3     # h3 origin on udp :8443
//      PLAYGROUND_UPSTREAM_PORT=8443 dotnet run -c Release --project Playground/Proxy/H2ToH3
//      curl -k --http2 https://127.0.0.1:8443/
//
//  Needs: ioxide, ioxide.nghttp2, ioxide.httpclient
// ─────────────────────────────────────────────────────────────────────────────────────────────

// ── Knobs ────────────────────────────────────────────────────────────────────────────────────
// Edit these. That is the whole mechanism - there is no config file and nothing else to find.
// Each Env.Override names the variable that can drive it instead, which is how the bench scripts
// run this sample; the literal is what applies otherwise. Delete those lines when you copy this
// out and the literals above them are the entire configuration.

int     reactors         = Environment.ProcessorCount;
ushort  port             = 8443;
string  upstreamHost     = "127.0.0.1";
ushort  upstreamPort     = 8444;
string  upstreamSni      = "localhost";
int     upstreamPool     = 1;
string? certOverride     = null;   // a real PEM pair, or null to self-sign on first run
string? keyOverride      = null;

Env.Override(ref reactors, "PLAYGROUND_REACTORS");
Env.Override(ref port, "PLAYGROUND_PORT");
Env.Override(ref upstreamHost, "PLAYGROUND_UPSTREAM_HOST");
Env.Override(ref upstreamPort, "PLAYGROUND_UPSTREAM_PORT");
Env.Override(ref upstreamSni, "PLAYGROUND_UPSTREAM_SNI");
Env.Override(ref upstreamPool, "PLAYGROUND_UPSTREAM_POOL");
Env.OverrideOptional(ref certOverride, "PLAYGROUND_QUIC_CERT");
Env.OverrideOptional(ref keyOverride, "PLAYGROUND_QUIC_KEY");
// ─────────────────────────────────────────────────────────────────────────────────────────────

(string certPath, string keyPath) = QuicCert.Ensure(certOverride, keyOverride);

var config = new ServerConfig
{
    ReactorCount   = reactors,
    RingEntries    = 8192,       // SQ/CQ depth per ring
    DualStack      = false,      // true = one IPv6 socket also accepts IPv4-mapped
    RecvBufferSize = 32 * 1024,  // bytes per shared recv buffer
    RecvSlots      = 4096,       // shared recv buffer-ring depth
    Incremental    = null,       // per-connection recv rings (6.12+) - see Tcp/Incremental
    Udp            = null,       // no raw UDP sockets (TCP-only frontend)
    Quic           = null,       // no QUIC listener; the frontend is TLS-over-TCP
    Tcp = new TcpOptions
    {
        Port             = port,
        ExtraPorts       = [],                                 // extra listener ports (one handler, several doors)
        ListenBacklog    = 1024,                               // accept-queue depth per SO_REUSEPORT listener
        WriteSlabSize    = 16 * 1024,                          // per-connection write buffer before overflow kicks in
        PoolMax          = 1024,                               // pooled connection objects kept per reactor
        WriteOverflow    = WriteOverflowStrategy.Grow,         // Grow = realloc one slab; Segmented = chain + vectored SENDMSG
        ZeroCopySend     = false,                              // SEND_ZC: kernel copies less, wins on large writes
        RecvQueueEntries = 64,                                 // per-connection recv completion queue depth
    },
};
string upstreamName = upstreamSni;    // sent as SNI, checked against the cert

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

        // Outbound: no TLS options, because QUIC has no cleartext mode - TLS 1.3 is inside the
        // transport and ALPN ("h3") is negotiated as part of the connection handshake. Opening
        // this pool is what makes the reactor grow its client-side QUIC transport.
        Http3ClientPool.Start(r, new Http3ClientOptions
        {
            Host             = upstreamHost,     // origin address (IPv4 literal; DNS would block the reactor)
            Port             = upstreamPort,     // origin's QUIC (UDP) port
            ServerName       = upstreamName,     // SNI / :authority, checked against the cert
            PoolSize         = upstreamPool,     // h3 multiplexes: one connection carries many streams
            AcquireTimeoutMs = 10_000,           // how long a request waits (handshake included)
            MaxResponseBytes = 8 * 1024 * 1024,  // per-request ceiling for headers + body
        });
    };

    reactor.TcpHandle = async (r, conn) =>
    {
        Http3ClientPool client = r.GetService<Http3ClientPool>();
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
                    // An h2 stream in, a QUIC stream out. Both are completions on this reactor's
                    // ring - one from a TCP recv, one from a UDP recv - and both resume inline.
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
            Console.Error.WriteLine($"[proxy h2->h3] connection failed: {e.Message}");
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

Console.WriteLine($"[proxy h2->h3] {config.ReactorCount} reactors, h2 over TLS on :{config.Tcp!.Port} "
                + $"-> h3 {upstreamName} ({upstreamHost}:udp {upstreamPort}), "
                + $"{upstreamPool} connection(s) each");

foreach (Thread thread in threads)
{
    thread.Join();
}
