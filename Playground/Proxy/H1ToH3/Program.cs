using System.Text;
using ioxide;
using ioxide.httpclient;
using ioxide.tls;
using ioxide.utils;
using Playground.Shared;

// ─────────────────────────────────────────────────────────────────────────────────────────────
//  proxy h1->h3 - HTTPS in over TCP, HTTP/3 out over QUIC. The upstream hop needs no TLS options
//  at all, because QUIC has no cleartext mode: TLS 1.3 is inside the transport, and the handshake
//  is the connection handshake. Http3ClientOptions carries ServerName for the same reason the h1
//  and h2 pools do - it is what the certificate has to match - and nothing else.
//
//  Note also what the config does NOT contain: no ServerConfig.Quic, no UDP ports, no certificate
//  for the upstream. Being an HTTP/3 client requires no HTTP/3 server - the first connect opens an
//  ephemeral UDP socket on this reactor's ring and replies route back by connection ID.
//
//      dotnet run -c Release --project Playground/Http3/Nghttp3     # h3 origin on udp :8443
//      PLAYGROUND_UPSTREAM_PORT=8443 dotnet run -c Release --project Playground/Proxy/H1ToH3
//      curl -k https://127.0.0.1:8443/
//
//  Two TLS stacks are in play. INBOUND is ioxide's own termination, OpenSSL in both directions
//  by default. OUTBOUND is the client pool's: each upstream connection wraps its own
//  TlsClientStream. Neither shows up in the proxying code.
//
//  Needs: ioxide, ioxide.httpclient
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
ushort upstreamPort = Env.Port("PLAYGROUND_UPSTREAM_PORT", 8444);       // the upstream's QUIC (UDP) port
string upstreamName = Env.Str("PLAYGROUND_UPSTREAM_SNI", "localhost");    // sent as SNI, checked against the cert
int upstreamPool = Env.Int("PLAYGROUND_UPSTREAM_POOL", 1);                // h3 multiplexes: one connection carries all

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
            CertificatePath = certPath,      // PEM cert chain file (set exactly one of Path/Pem)
            CertificatePem  = null,          // in-memory PEM alternative to CertificatePath
            KeyPath         = keyPath,       // PEM private key file (set exactly one of Path/Pem)
            KeyPem          = null,          // in-memory PEM alternative to KeyPath
            Alpn            = ["http/1.1"],  // protocols offered, most preferred first
            KernelTx        = false,         // kTLS encrypt (off = OpenSSL both ways)
            KernelRx        = false,         // kTLS decrypt; requires KernelTx, experimental
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
            // The handshake reads and writes through this same connection.
            tls = await r.GetService<TlsService>().AcceptAsync(conn);

            // A request can ride in with the handshake's final flight - answer it before parking
            // in ReadAsync, or the client waits on a response we never send.
            if (TryReadTarget(tls.DrainPlaintext(), out ReadOnlySpan<byte> early))
            {
                await ProxyAsync(conn, tls, client, Encoding.ASCII.GetString(early));
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
                    await ProxyAsync(conn, tls, client, path);
                }

                if (snapshot.IsClosed || tls.Closed) return;
                conn.ResetRead();
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[proxy h1->h3] connection failed: {e.Message}");
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

Console.WriteLine($"[proxy h1->h3] {config.ReactorCount} reactors, https on :{config.Tcp!.Port} "
                + $"-> h3 {upstreamName} ({upstreamHost}:udp {upstreamPort}), "
                + $"{upstreamPool} connection(s) each");

foreach (Thread thread in threads)
{
    thread.Join();
}

// One request out, one response back. tls.Write encrypts correctly whichever backend the
// session ended up with.
static async ValueTask ProxyAsync(TcpConnection conn, TlsSession tls, Http3ClientPool client, string path)
{
    try
    {
        // The outbound call is a QUIC stream. Both hops are completions on this same ring - one
        // from a TCP recv, one from a UDP recv - and both resume inline.
        using HttpClientResponse response = await client.GetAsync(path);

        tls.Write(conn, Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {response.Status} OK\r\nContent-Length: {response.Body.Length}\r\n\r\n"));
        tls.Write(conn, response.Body.Span);   // bytes straight through, no decode
    }
    catch (Exception e)
    {
        // A dead origin surfaces here rather than hanging: the pool bounds the whole acquire. A
        // refused certificate arrives the same way - for QUIC the TLS handshake IS the connection
        // handshake, so it fails as a failed connect.
        byte[] message = Encoding.ASCII.GetBytes($"upstream: {e.Message}");
        tls.Write(conn, Encoding.ASCII.GetBytes(
            $"HTTP/1.1 502 Bad Gateway\r\nContent-Length: {message.Length}\r\n\r\n"));
        tls.Write(conn, message);
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
