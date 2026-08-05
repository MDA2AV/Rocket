using System.Text;
using ioxide;
using ioxide.httpclient;
using ioxide.utils;
using Playground.Shared;

// ─────────────────────────────────────────────────────────────────────────────────────────────
//  proxy - a reverse proxy, whole. Every inbound request is forwarded to an upstream origin
//  through the ring-native HTTP client and the response is relayed back. BOTH hops - the inbound
//  connection and the outbound call - run on this reactor's ring and resume inline, so a proxied
//  request never leaves the thread it arrived on. No thread pool, no cross-core handoff.
//
//      # terminal 1: an origin to forward to
//      PLAYGROUND_PORT=8081 dotnet run -c Release --project Playground/Tcp/Raw
//      # terminal 2: the proxy
//      dotnet run -c Release --project Playground/Proxy/H1
//      curl http://127.0.0.1:8080/
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

var upstream = new HttpClientOptions
{
    Host = Env.Str("PLAYGROUND_UPSTREAM_HOST", "127.0.0.1"),   // IPv4 literal: DNS would block the reactor
    Port = Env.Port("PLAYGROUND_UPSTREAM_PORT", 8081),
    PoolSize = Env.Int("PLAYGROUND_UPSTREAM_POOL", 8),         // keep-alive connections per reactor
};

var threads = new Thread[config.ReactorCount];

for (int i = 0; i < threads.Length; i++)
{
    var reactor = new Reactor(i, config);

    // Opened on the reactor thread, so the outbound sockets ride this reactor's ring too.
    reactor.OnStart = r => HttpClientPool.Start(r, upstream);

    reactor.TcpHandle = async (r, conn) =>
    {
        HttpClientPool client = r.GetService<HttpClientPool>();

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
                    // The outbound call. Same ring, same thread - this await resumes inline.
                    // The response owns its bytes, so dispose it when you're done with them.
                    using HttpClientResponse response = await client.GetAsync(path);

                    conn.Write(Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 {response.Status} OK\r\nContent-Length: {response.Body.Length}\r\n\r\n"));
                    conn.Write(response.Body.Span);   // bytes straight through, no decode
                }
                catch (HttpClientException e)
                {
                    // A dead origin surfaces here rather than hanging: the pool bounds the whole
                    // acquire, so you get an exception and can answer with a 502.
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

Console.WriteLine($"[proxy] {config.ReactorCount} reactors on :{config.Tcp.Port} "
                + $"-> {upstream.Host}:{upstream.Port}, {upstream.PoolSize} connections each");

foreach (Thread thread in threads)
{
    thread.Join();
}

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
