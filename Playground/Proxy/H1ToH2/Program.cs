using System.Text;
using ioxide;
using ioxide.httpclient;
using ioxide.utils;
using Playground.Shared;

// ─────────────────────────────────────────────────────────────────────────────────────────────
//  proxy h1->h2 - a plain HTTP/1.1 server whose upstream calls ride HTTP/2 (h2c). The frontend is
//  Proxy.H1ToH1's, byte for byte; only the pool changed. That is the point of the matrix: the
//  protocol on each hop is a pool type, not a rewrite.
//
//  What the swap buys is fan-in. h1 needs one upstream connection per in-flight request, so the
//  pool sizes for concurrency; h2 multiplexes, so PoolSize 1 carries every concurrent request on
//  a single socket. A proxy in front of a few origins can hold one connection to each.
//
//      # the h2c upstream, moved off 8080 so the proxy can have it
//      PLAYGROUND_PORT=8081 dotnet run -c Release --project Playground/Http2/Nghttp2
//      dotnet run -c Release --project Playground/Proxy/H1ToH2
//      curl http://127.0.0.1:8080/anything
//
//  Needs: ioxide.httpclient
// ─────────────────────────────────────────────────────────────────────────────────────────────

var config = new ServerConfig
{
    ReactorCount = Env.Int("PLAYGROUND_REACTORS", Environment.ProcessorCount),
    Tcp = new TcpOptions
    {
        Port = Env.Port("PLAYGROUND_PORT", 8080),
    },
};

var upstream = new Http2ClientOptions
{
    Host = Env.Str("PLAYGROUND_UPSTREAM_HOST", "127.0.0.1"),   // IPv4 literal: DNS would block the reactor
    Port = Env.Port("PLAYGROUND_UPSTREAM_PORT", 8081),
    PoolSize = Env.Int("PLAYGROUND_UPSTREAM_POOL", 1),         // h2 multiplexes: one connection carries all
};

var threads = new Thread[config.ReactorCount];

for (int i = 0; i < threads.Length; i++)
{
    var reactor = new Reactor(i, config);

    // Opened on the reactor thread, so the upstream socket rides this reactor's ring too.
    reactor.OnStart = r => Http2ClientPool.Start(r, upstream);

    reactor.TcpHandle = async (r, conn) =>
    {
        Http2ClientPool client = r.GetService<Http2ClientPool>();

        try
        {
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
                    // h1 request in, h2 stream out - same ring, same thread, inline resume.
                    using HttpClientResponse response = await client.GetAsync(path);

                    conn.Write(Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 {response.Status} OK\r\nContent-Length: {response.Body.Length}\r\n\r\n"));
                    conn.Write(response.Body.Span);   // bytes straight through, no decode
                }
                catch (Exception e)
                {
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

Console.WriteLine($"[proxy h1->h2] {config.ReactorCount} reactors on :{config.Tcp!.Port} "
                + $"-> h2c {upstream.Host}:{upstream.Port}, {upstream.PoolSize} connection(s) each");

foreach (Thread thread in threads)
{
    thread.Join();
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
