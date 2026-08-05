using System.Text;
using ioxide;
using ioxide.http11;
using ioxide.httpclient;
using ioxide.utils;
using Playground.Shared;

// ─────────────────────────────────────────────────────────────────────────────────────────────
//  proxy h1->h3 - a plain HTTP/1.1 server whose upstream calls ride HTTP/3. Note what the
//  config does NOT contain: no ServerConfig.Quic, no UDP ports, no certificate. Dialing h3 out
//  needs none of it - the first connect makes the reactor open a UDP socket on an EPHEMERAL
//  port (the standalone shape of QUIC client mode), and replies route back by connection ID.
//  Being an h3 client requires no h3 server.
//
//      dotnet run -c Release --project Playground/Nghttp3        # the h3 upstream on udp :8443
//      dotnet run -c Release --project Playground/Proxy/H1ToH3
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

var upstream = new Http3ClientOptions
{
    Host = Env.Str("PLAYGROUND_UPSTREAM_HOST", "127.0.0.1"),   // IPv4 literal: DNS would block the reactor
    Port = Env.Port("PLAYGROUND_UPSTREAM_PORT", 8443),         // the upstream's QUIC (UDP) port
    ServerName = Env.Str("PLAYGROUND_UPSTREAM_SNI", "localhost"),
    PoolSize = Env.Int("PLAYGROUND_UPSTREAM_POOL", 1),         // h3 multiplexes: one connection carries all
};

var threads = new Thread[config.ReactorCount];

for (int i = 0; i < threads.Length; i++)
{
    var reactor = new Reactor(i, config);

    // Opening the pool here is what makes the reactor grow its client-side QUIC transport: an
    // ephemeral UDP socket, armed on this ring, shared by every upstream connection.
    reactor.OnStart = r => Http3ClientPool.Start(r, upstream);

    reactor.TcpHandle = async (r, conn) =>
    {
        Http3ClientPool client = r.GetService<Http3ClientPool>();

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
                    // h1 request in, h3 request out - same ring, same thread, inline resume.
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

Console.WriteLine($"[proxy h1->h3] {config.ReactorCount} reactors on :{config.Tcp!.Port} "
                + $"-> h3 {upstream.Host}:{upstream.Port} (client-only QUIC, ephemeral port)");

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
