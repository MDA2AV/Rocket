using System.Text;
using ioxide;
using ioxide.httpclient;
using ioxide.utils;
using Playground.Shared;

// ─────────────────────────────────────────────────────────────────────────────────────────────
//  http - a reverse proxy that lets the ORIGIN pick the protocol. Both hops - the inbound
//  connection and the outbound call - run on this reactor's ring and resume inline, so a request
//  never leaves the thread it arrived on.
//
//  The knob that matters is Policy. Negotiate starts on HTTP/1.1 and switches to HTTP/3 once the
//  origin advertises it via Alt-Svc (RFC 7838), so the upgrade costs nothing at the call site -
//  the same GetAsync is h1 on the first request and h3 later. Http1Only / Http2Only (h2c) /
//  Http3Only pin it instead, which is what the nine Proxy/* samples do.
//
//      dotnet run -c Release --project Playground/Http3/Nghttp3   # an origin that advertises h3
//      PLAYGROUND_UPSTREAM_PORT=8080 dotnet run -c Release --project Playground/Clients/Http
//      curl http://127.0.0.1:8090/
//
//  Needs: ioxide, ioxide.httpclient
// ─────────────────────────────────────────────────────────────────────────────────────────────

// ── Knobs ────────────────────────────────────────────────────────────────────────────────────
// Edit these. That is the whole mechanism - there is no config file and nothing else to find.
// Env.Override exists only so bench/run.sh can drive the sample from outside; delete that line
// when you copy this out and the literals above it are the entire configuration.

ushort port     = 8090;                        // http://127.0.0.1:8090/ - what this proxy serves
int    reactors = Environment.ProcessorCount;  // one ring per reactor, one reactor per core

Env.Override(ref port, ref reactors);

// The origin this forwards to. The host must be an IPv4 literal - a DNS lookup would block the
// reactor - and PoolSize is per protocol, per reactor.
string upstreamHost = "127.0.0.1";
ushort upstreamPort = 8081;
int    upstreamPool = 8;

// The path every inbound request is forwarded to, whatever was asked for.
string upstreamPath = "/";
// ─────────────────────────────────────────────────────────────────────────────────────────────

var config = new ServerConfig
{
    ReactorCount = reactors,
    RingEntries  = 8192,                         // io_uring SQ/CQ depth
    DualStack    = false,                        // true = one AF_INET6 socket takes v6 + v4-mapped

    // Shared recv ring - the default mode, used while Incremental is null.
    RecvBufferSize = 32 * 1024,
    RecvSlots      = 4096,

    Tcp = new TcpOptions
    {
        Port             = port,
        ExtraPorts       = [],                   // more listeners; conn.ListenerPort says which
        ListenBacklog    = 1024,                 // accept queue per SO_REUSEPORT listener
        WriteSlabSize    = 16 * 1024,            // per-connection write buffer
        PoolMax          = 1024,                 // connection objects recycled per reactor
        WriteOverflow    = WriteOverflowStrategy.Grow,   // Segmented = chained slabs, one SENDMSG
        ZeroCopySend     = false,                // SEND_ZC; only pays off on large responses
        RecvQueueEntries = 64,                   // per-connection SPSC queue, power of two
    },
};

var upstream = new RingHttpClientOptions
{
    Host     = upstreamHost,
    Port     = upstreamPort,
    PoolSize = upstreamPool,

    // Start on HTTP/1.1, switch to HTTP/3 once the origin advertises it via Alt-Svc.
    Policy = HttpProtocolPolicy.Negotiate,
};

var threads = new Thread[config.ReactorCount];

for (int id = 0; id < threads.Length; id++)
{
    var reactor = new Reactor(id, config);

    // The pool opens its connections on THIS reactor's ring, which is what keeps both hops on
    // one thread.
    reactor.OnStart = r => RingHttpClient.Start(r, upstream);

    reactor.TcpHandle = async (r, conn) =>
    {
        RingHttpClient http = r.GetService<RingHttpClient>();

        try
        {
            while (true)
            {
                RecvSnapshot snapshot = await conn.ReadAsync();

                // This sample forwards a fixed path, so the request bytes are drained rather than
                // parsed - the Proxy/* samples show real target extraction.
                while (conn.TryGetItem(snapshot, out SpscRecvRing.Item item))
                {
                    if (item.HasBuffer) conn.ReturnBuffer(in item);
                }

                try
                {
                    // The response owns its bytes - dispose it when done.
                    using HttpClientResponse response = await http.GetAsync(upstreamPath);

                    conn.Write(Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 {response.Status} OK\r\nContent-Length: {response.Body.Length}\r\n\r\n"));
                    conn.Write(response.Body.Span);   // bytes straight through, no decode
                }
                catch (Exception e)
                {
                    // A dead origin surfaces here rather than hanging: the pool bounds the acquire.
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

    threads[id] = new Thread(reactor.Run) { Name = $"reactor-{id}" };
    threads[id].Start();
}

Console.WriteLine($"[http] {config.ReactorCount} reactors on :{config.Tcp!.Port} -> "
                + $"{upstream.Host}:{upstream.Port} (policy {upstream.Policy})");

foreach (Thread thread in threads)
{
    thread.Join();
}
