using System.Text;
using ioxide;
using ioxide.httpclient;
using ioxide.nghttp2;
using ioxide.tls;
using Playground.Shared;

// ─────────────────────────────────────────────────────────────────────────────────────────────
//  proxy h2->h1 - an HTTP/2 front door for an HTTP/1.1 origin, TLS on both hops. This is the
//  classic edge shape: clients get multiplexing and header compression over a real https:// URL,
//  the origin keeps speaking the protocol it already speaks.
//
//  Browsers will not speak h2 in cleartext, so this is the only way the frontend is reachable
//  from one. ALPN is what makes it work: the server offers "h2" and the client picks it during
//  the handshake, before a single byte of HTTP exists.
//
//  Note what Nghttp2Connection is handed - a TlsConnectionDualPipe. It never learns that TLS is
//  involved: the pipe decrypts on the way in and encrypts on the way out, so the HTTP/2 code
//  is byte-for-byte the h2c version.
//
//      # a TLS origin to forward to
//      PLAYGROUND_PORT=8444 dotnet run -c Release --project Playground/Tls/OpenSsl
//      dotnet run -c Release --project Playground/Proxy/H2ToH1
//      curl -k --http2 https://127.0.0.1:8443/
//
//  Note the asymmetry this combination creates. One h2 connection can have a hundred streams in
//  flight, and each one needs its own h1 upstream connection for the duration - h1 has no
//  multiplexing to borrow. So the pool sizes for concurrency here, unlike every h2/h3 upstream in
//  this folder. Run out and the request queues behind a waiter rather than the pool opening
//  unbounded, with the whole acquire bounded by HttpClientOptions.AcquireTimeoutMs - a saturated
//  origin surfaces as a 502 on that stream, not as an fd leak.
//
//  Needs: ioxide, ioxide.nghttp2, ioxide.httpclient
// ─────────────────────────────────────────────────────────────────────────────────────────────

(string certPath, string keyPath) = QuicCert.Ensure(
    Env.StrOrNull("PLAYGROUND_TLS_CERT"),
    Env.StrOrNull("PLAYGROUND_TLS_KEY"));

var config = new ServerConfig
{
    ReactorCount = Env.Int("PLAYGROUND_REACTORS", Environment.ProcessorCount),
    Tcp = new TcpOptions
    {
        Port = Env.Port("PLAYGROUND_PORT", 8443),
    },
};

string upstreamHost = Env.Str("PLAYGROUND_UPSTREAM_HOST", "127.0.0.1");   // IPv4 literal: DNS would block the reactor
ushort upstreamPort = Env.Port("PLAYGROUND_UPSTREAM_PORT", 8444);
string upstreamName = Env.Str("PLAYGROUND_UPSTREAM_SNI", "localhost");    // sent as SNI, checked against the cert
int upstreamPool = Env.Int("PLAYGROUND_UPSTREAM_POOL", 32);               // h1 has no multiplexing: size for concurrency

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
            CertificatePath = certPath,
            KeyPath = keyPath,
            Alpn = ["h2"],
        });

        // Outbound: one context per reactor, shared by that reactor's pool - it holds no
        // per-connection state, so every connection opened from it gets its own SSL.
        HttpClientPool.Start(r, new HttpClientOptions
        {
            Host = upstreamHost,
            Port = upstreamPort,
            PoolSize = upstreamPool,
            Tls = TlsClientContext.Create(new TlsClientOptions
            {
                ServerName = upstreamName,
                AlpnProtocols = ["http/1.1"],
                CaFile = upstreamInsecure ? null : upstreamCa,
                VerifyCertificate = !upstreamInsecure,
            }),
        });
    };

    reactor.TcpHandle = async (r, conn) =>
    {
        HttpClientPool client = r.GetService<HttpClientPool>();
        TlsSession? tls = null;

        try
        {
            tls = await r.GetService<TlsService>().AcceptAsync(conn);

            // The decrypt pump lives in the pipe, so everything below is the cleartext sample -
            // and the pipe resumes inline on this reactor, so the upstream call below does too.
            await using var pipe = new TlsConnectionDualPipe(conn, tls, ownsSession: false);

            // Buffered + async: each stream dispatches with its body assembled, and the handler
            // may await - the upstream round trip resumes inline on this reactor. Concurrent
            // streams interleave here, which is exactly why the h1 pool has to be deep.
            await new Nghttp2Connection(pipe).RunBufferedAsync(async request =>
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
            Console.Error.WriteLine($"[proxy h2->h1] connection failed: {e.Message}");
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

Console.WriteLine($"[proxy h2->h1] {config.ReactorCount} reactors, h2 over TLS on :{config.Tcp!.Port} "
                + $"-> https://{upstreamName} ({upstreamHost}:{upstreamPort}), "
                + $"{upstreamPool} connections each, "
                + $"verify={(upstreamInsecure ? "OFF" : upstreamCa ?? "system store")}");

foreach (Thread thread in threads)
{
    thread.Join();
}
