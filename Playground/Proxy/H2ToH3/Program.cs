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
//  Both hops are TLS 1.3 and they get there completely differently. INBOUND is kTLS on a TCP
//  socket: OpenSSL handshake over the ring, then the kernel owns transmit. OUTBOUND is QUIC,
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
//  Needs the Linux 'tls' module (sudo modprobe tls). Needs: ioxide, ioxide.nghttp2, ioxide.httpclient
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
ushort upstreamPort = Env.Port("PLAYGROUND_UPSTREAM_PORT", 8444);       // the upstream's QUIC (UDP) port
string upstreamName = Env.Str("PLAYGROUND_UPSTREAM_SNI", "localhost");    // sent as SNI, checked against the cert
int upstreamPool = Env.Int("PLAYGROUND_UPSTREAM_POOL", 1);                // h3 multiplexes: one connection carries all

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

        // Outbound: no TLS options, because QUIC has no cleartext mode - TLS 1.3 is inside the
        // transport and ALPN ("h3") is negotiated as part of the connection handshake. Opening
        // this pool is what makes the reactor grow its client-side QUIC transport.
        Http3ClientPool.Start(r, new Http3ClientOptions
        {
            Host = upstreamHost,
            Port = upstreamPort,
            ServerName = upstreamName,
            PoolSize = upstreamPool,
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
