using System.Text;
using ioxide;
using ioxide.httpclient;
using ioxide.nghttp2;
using Playground.Shared;

// ─────────────────────────────────────────────────────────────────────────────────────────────
//  proxy h2->h3 - HTTP/2 in over TCP, HTTP/3 out over QUIC. The protocol-translating edge: the
//  client half of a migration where the origin has moved to h3 and the clients have not.
//
//  Look at what the config does NOT contain: no ServerConfig.Quic, no UDP ports, no certificate.
//  Dialing h3 out needs none of it - the first connect makes the reactor open a UDP socket on an
//  EPHEMERAL port, and replies route back by connection ID. Being an h3 client requires no h3
//  server, exactly as in Proxy.H1ToH3; the frontend changing from h1 to h2 changes nothing about
//  that half.
//
//      dotnet run -c Release --project Playground/Http3/Nghttp3      # the h3 upstream on udp :8443
//      dotnet run -c Release --project Playground/Proxy/H2ToH3
//      curl --http2-prior-knowledge http://127.0.0.1:8080/anything
//
//  Needs: ioxide.nghttp2, ioxide.httpclient
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
            await new Nghttp2Connection(conn).RunBufferedAsync(async request =>
            {
                try
                {
                    // An h2 stream in, a QUIC stream out. Both are completions on this reactor's
                    // ring - one from a TCP recv, one from a UDP recv - and both resume inline.
                    using HttpClientResponse response = await client.SendAsync(new HttpClientRequest(
                        request.Method, request.Path) { Body = request.Body });

                    // Copy before Dispose: the response arena is freed then, and nghttp2 copies
                    // the h2 response only AFTER this handler returns.
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
                    return new Nghttp2Response
                    {
                        Status = 502,
                        Body = Encoding.ASCII.GetBytes($"upstream failed: {e.Message}\n"),
                    };
                }
            });
        }
        finally
        {
            conn.DecRef();
        }
    };

    threads[i] = new Thread(reactor.Run) { Name = $"reactor-{i}" };
    threads[i].Start();
}

Console.WriteLine($"[proxy h2->h3] {config.ReactorCount} reactors, h2c on :{config.Tcp!.Port} "
                + $"-> h3 {upstream.Host}:{upstream.Port} (client-only QUIC, ephemeral port)");

foreach (Thread thread in threads)
{
    thread.Join();
}
