using System.Text;
using ioxide;
using ioxide.httpclient;
using ioxide.tls;
using ioxide.utils;
using Playground.Shared;

// ─────────────────────────────────────────────────────────────────────────────────────────────
//  tls-httpsclient - the OTHER direction of ioxide.tls: not terminating TLS for inbound
//  connections, but speaking it outbound so a handler can reach an https:// origin.
//
//      dotnet run -c Release --project Playground/Tls/HttpsClient
//      curl http://127.0.0.1:8080/get
//
//      PLAYGROUND_ORIGIN_IP=93.184.216.34   the origin to call (IPv4 literal: DNS would block
//      PLAYGROUND_ORIGIN_NAME=example.com   the reactor). The NAME is what gets sent as SNI and
//      PLAYGROUND_ORIGIN_PORT=443           what the certificate has to match.
//      PLAYGROUND_CA=/path/ca.pem           trust a private CA instead of the system store
//      PLAYGROUND_INSECURE=1                skip verification entirely (see the warning below)
//
//  The TLS context is built once and shared by the pool: it holds no per-connection state, so
//  every connection opened from it gets its own SSL. The handshake rides the reactor's ring like
//  every other I/O here, and the response resumes this handler inline on its own thread.
//
//  Unlike the server side, none of this needs kTLS - nothing is offloaded to the kernel, so it
//  runs without 'modprobe tls'. Needs: ioxide, ioxide.tls, ioxide.httpclient
// ─────────────────────────────────────────────────────────────────────────────────────────────

string originIp   = Env.Str("PLAYGROUND_ORIGIN_IP", "127.0.0.1");
string originName = Env.Str("PLAYGROUND_ORIGIN_NAME", "localhost");
ushort originPort = Env.Port("PLAYGROUND_ORIGIN_PORT", 443);
string? caFile    = Env.StrOrNull("PLAYGROUND_CA");
bool insecure     = Env.Flag("PLAYGROUND_INSECURE");

var config = new ServerConfig
{
    ReactorCount = Env.Int("PLAYGROUND_REACTORS", Environment.ProcessorCount),
    Tcp = new TcpOptions
    {
        Port = Env.Port("PLAYGROUND_PORT", 8080),
    },
};

var threads = new Thread[config.ReactorCount];

for (int i = 0; i < threads.Length; i++)
{
    var reactor = new Reactor(i, config);

    reactor.OnStart = r =>
    {
        // One context per reactor, shared by that reactor's pool. Verification is on by default;
        // turning it off leaves the connection encrypted but UNAUTHENTICATED, so anything in the
        // path can present its own certificate and read or rewrite the whole exchange. A private
        // CA belongs in CaFile, not here.
        TlsClientContext tls = TlsClientContext.Create(new TlsClientOptions
        {
            ServerName        = originName,
            AlpnProtocols     = ["http/1.1"],
            CaFile            = caFile,
            VerifyCertificate = !insecure,
        });

        HttpClientPool.Start(r, new HttpClientOptions
        {
            Host     = originIp,
            Port     = originPort,
            PoolSize = 2,
            Tls      = tls,
        });
    };

    reactor.TcpHandle = async (r, conn) =>
    {
        try
        {
            HttpClientPool upstream = r.GetService<HttpClientPool>()!;

            while (true)
            {
                RecvSnapshot snapshot = await conn.ReadAsync();

                string path = "/";
                while (conn.TryGetItem(snapshot, out SpscRecvRing.Item item))
                {
                    if (item.HasBuffer)
                    {
                        if (TryReadTarget(item.AsSpan(), out ReadOnlySpan<byte> target))
                        {
                            path = Encoding.ASCII.GetString(target);
                        }
                        conn.ReturnBuffer(in item);
                    }
                }

                try
                {
                    // Encrypted on the way out, decrypted on the way back, and neither shows up
                    // here: the pool hands back the same HttpClientResponse a cleartext origin
                    // would have produced.
                    using HttpClientResponse response = await upstream.GetAsync(path);

                    conn.Write(Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 200 OK\r\nContent-Length: {response.Body.Length}\r\n\r\n"));
                    conn.Write(response.Body.Span);
                }
                catch (Exception e)
                {
                    // A refused certificate arrives here like any other upstream failure - the
                    // handshake is part of opening the connection, so the pool reports it the same
                    // way it reports a dead origin.
                    byte[] message = Encoding.ASCII.GetBytes($"upstream: {e.Message}");
                    conn.Write(Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 502 Bad Gateway\r\nContent-Length: {message.Length}\r\n\r\n"));
                    conn.Write(message);
                }

                await conn.FlushAsync();

                if (snapshot.IsClosed) return;
                conn.ResetRead();
            }
        }
        finally
        {
            conn.DecRef();
        }
    };

    threads[i] = new Thread(reactor.Run) { Name = $"reactor-{i}" };
    threads[i].Start();
}

Console.WriteLine($"[tls-httpsclient] {config.ReactorCount} reactors on :{config.Tcp!.Port}, "
                + $"origin https://{originName} ({originIp}:{originPort}), "
                + $"verify={(insecure ? "OFF" : caFile ?? "system store")}");

foreach (Thread thread in threads)
{
    thread.Join();
}

// "GET /path?query HTTP/1.1" -> "/path"
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
