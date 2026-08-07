using System.Text;
using ioxide;
using ioxide.httpclient;
using ioxide.tls;
using ioxide.utils;
using Playground.Shared;

// ─────────────────────────────────────────────────────────────────────────────────────────────
//  proxy h1->h2 - HTTPS in, h2-over-TLS out. Identical to Proxy.H1ToH1 except for the upstream:
//  the pool type changed, its ALPN offer changed from "http/1.1" to "h2", and PoolSize dropped to
//  1 because h2 multiplexes - one upstream connection carries every concurrent request.
//
//  ALPN is what selects h2 here. Offering it is not optional: an origin that hears no ALPN has to
//  assume a protocol, and assuming h2 is not something any origin does.
//
//      # an h2-over-TLS origin to forward to
//      PLAYGROUND_PORT=8444 dotnet run -c Release --project Playground/Http2/Tls
//      dotnet run -c Release --project Playground/Proxy/H1ToH2
//      curl -k https://127.0.0.1:8443/
//
//  Two different TLS stacks are in play and they are not symmetric. INBOUND is kTLS: the OpenSSL
//  handshake runs over the ring, then the kernel takes over transmit, so everything this handler
//  writes is PLAINTEXT and the kernel makes the records. OUTBOUND is userspace: the pool wraps
//  each upstream connection in its own TlsClientStream. Neither shows up in the proxying code.
//
//  Needs the Linux 'tls' module (sudo modprobe tls). Needs: ioxide, ioxide.httpclient
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
        // Inbound: terminate TLS for clients. ALPN is an ordered list and this proxy speaks one
        // protocol, so it offers exactly one.
        TlsService.Start(r, new TlsOptions
        {
            CertificatePath = certPath,
            KeyPath = keyPath,
            Alpn = ["http/1.1"],
        });

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

    reactor.TcpHandle = async (r, conn) =>
    {
        Http2ClientPool client = r.GetService<Http2ClientPool>();
        TlsSession? tls = null;

        try
        {
            // The handshake reads and writes through this same connection; after it, the socket
            // carries kTLS records the kernel en/decrypts.
            tls = await r.GetService<TlsService>().AcceptAsync(conn);

            // A request can ride in with the handshake's final flight - answer it before parking
            // in ReadAsync, or the client waits on a response we never send.
            if (TryReadTarget(tls.DrainPlaintext(), out ReadOnlySpan<byte> early))
            {
                await ProxyAsync(conn, client, Encoding.ASCII.GetString(early));
            }

            while (true)
            {
                RecvSnapshot snapshot = await conn.ReadAsync();

                string? path = null;
                unsafe
                {
                    while (conn.TryGetItem(snapshot, out SpscRecvRing.Item item))
                    {
                        if (item.HasBuffer)
                        {
                            // Records in, plaintext out - the session decrypts what the ring got.
                            if (TryReadTarget(tls.Decrypt(item.Ptr, item.Len), out ReadOnlySpan<byte> target))
                            {
                                path = Encoding.ASCII.GetString(target);
                            }
                            conn.ReturnBuffer(in item);
                        }
                    }
                }

                if (path is not null)
                {
                    await ProxyAsync(conn, client, path);
                }

                if (snapshot.IsClosed || tls.Closed) return;
                conn.ResetRead();
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[proxy h1->h2] connection failed: {e.Message}");
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

Console.WriteLine($"[proxy h1->h2] {config.ReactorCount} reactors, https on :{config.Tcp!.Port} "
                + $"-> h2 https://{upstreamName} ({upstreamHost}:{upstreamPort}), "
                + $"{upstreamPool} connection(s) each, "
                + $"verify={(upstreamInsecure ? "OFF" : upstreamCa ?? "system store")}");

foreach (Thread thread in threads)
{
    thread.Join();
}

// One request out, one response back. Everything written here is PLAINTEXT: after the handshake
// kTLS owns transmit, so there is nothing left for us to wrap.
static async ValueTask ProxyAsync(TcpConnection conn, Http2ClientPool client, string path)
{
    try
    {
        // The outbound call is an h2 stream on a shared connection, not a socket of its own.
        // Same ring, same thread - this await resumes inline.
        using HttpClientResponse response = await client.GetAsync(path);

        conn.Write(Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {response.Status} OK\r\nContent-Length: {response.Body.Length}\r\n\r\n"));
        conn.Write(response.Body.Span);   // bytes straight through, no decode
    }
    catch (Exception e)
    {
        // A dead origin surfaces here rather than hanging: the pool bounds the whole acquire. A
        // REFUSED CERTIFICATE arrives the same way - the handshake is part of opening the
        // connection, so a name mismatch reads like any other upstream failure.
        byte[] message = Encoding.ASCII.GetBytes($"upstream: {e.Message}");
        conn.Write(Encoding.ASCII.GetBytes(
            $"HTTP/1.1 502 Bad Gateway\r\nContent-Length: {message.Length}\r\n\r\n"));
        conn.Write(message);
    }

    await conn.FlushAsync();
}

// "GET /sleep?x=1 HTTP/1.1" -> "/sleep". Your framework of choice would do this for you; ioxide
// deliberately doesn't, so here it is in full.
static bool TryReadTarget(ReadOnlySpan<byte> request, out ReadOnlySpan<byte> target)
{
    target = default;

    int firstSpace = request.IndexOf((byte)' ');
    if (firstSpace < 0) return false;

    ReadOnlySpan<byte> afterMethod = request[(firstSpace + 1)..];
    int secondSpace = afterMethod.IndexOf((byte)' ');
    if (secondSpace < 0) return false;

    target = afterMethod[..secondSpace];

    int query = target.IndexOf((byte)'?');
    if (query >= 0) target = target[..query];

    return true;
}
