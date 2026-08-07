using System.Runtime.InteropServices;
using ioxide;
using ioxide.nghttp3;
using ioxide.ngtcp2;
using ioxide.utils;
using Playground.Shared;

// ─────────────────────────────────────────────────────────────────────────────────────────────
//  nghttp3-buffered - the same HTTP/3 server as Playground/Http3/Nghttp3, with the OTHER dispatch mode.
//
//  BUFFERED: dispatch waits for end-of-stream, so the whole body is already in request.Body when
//  your handler runs - no BodyReader, no pacing - and the handler may still await (a PgPool query,
//  Redis, anything ioxide-native resumes inline on the reactor).
//
//  The trade: memory holds the entire body, so this suits normal-sized requests. Use Playground/Http3/Nghttp3
//  when uploads can be large or hostile.
//
//      dotnet run -c Release --project Playground/Http3/Buffered
//      curl --http3-only -k https://127.0.0.1:8443/
//
//  Needs: ioxide, ioxide.ngtcp2, ioxide.nghttp3
// ─────────────────────────────────────────────────────────────────────────────────────────────

(string certPath, string keyPath) = QuicCert.Ensure(
    Env.StrOrNull("PLAYGROUND_QUIC_CERT"),
    Env.StrOrNull("PLAYGROUND_QUIC_KEY"));

using var engine = new QuicEngine(certPath, keyPath, cidLength: 8, alpn: ["h3"]);

ushort quicPort = Env.Port("PLAYGROUND_QUIC_PORT", 8443);

var config = new ServerConfig
{
    ReactorCount = Env.Int("PLAYGROUND_REACTORS", Environment.ProcessorCount),
    Tcp = new TcpOptions { Port = Env.Port("PLAYGROUND_PORT", 8080) },
    Udp = new UdpOptions { RecvSlots = Env.Int("PLAYGROUND_UDP_SLOTS", 16) },
    Quic = new QuicOptions
    {
        Port = quicPort,
        LocalCidLength = 8,
        ConnectionFactory = engine.CreateFactory(),
    },
};

long qpackCapacity = Env.Long("PLAYGROUND_QPACK_CAP", 0);
var h3Options = new Nghttp3Options
{
    QpackDynamicTableCapacity = qpackCapacity,
    QpackBlockedStreams = qpackCapacity > 0 ? 100 : 0,
};

// Built once and reused for every request - the h3 layer copies it into nghttp3 at submit and never
// retains it, so this costs zero allocations per request.
var response = new Nghttp3Response { Body = "Hello, World!"u8.ToArray() };
response.Headers.Add("content-type"u8.ToArray(), "text/plain"u8.ToArray());
response.Headers.Add("server"u8.ToArray(), "ioxide"u8.ToArray());

byte[] tcpResponse = "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 2\r\n\r\nok"u8.ToArray();

List<(Reactor Reactor, Nghttp3Connection Connection)> live = [];

var threads = new Thread[config.ReactorCount];

for (int i = 0; i < threads.Length; i++)
{
    var reactor = new Reactor(i, config);

    reactor.QuicHandle = (r, quicConn) =>
    {
        var h3 = new Nghttp3Connection(quicConn, h3Options);
        lock (live)
        {
            live.Add((r, h3));
        }

        // RunBufferedAsync, not RunStreamingAsync - that one call is the whole difference.
        return h3.RunBufferedAsync(request =>
        {
            // Dispatch waited for end-of-stream, so the body is ALREADY here: request.Body is
            // complete and request.Body.Length is just a property read. No BodyReader, no pacing.
            // This overload is synchronous, but the awaiting one exists too - a PgPool query or a
            // Redis command resumes inline on this reactor, so you can await it right here.
            _ = request.Body.Length;

            // One response object, reused for every request: zero allocations on this path. To
            // route, compare request.Path.Span - it is post-QPACK bytes, so SequenceEqual against a
            // u8 literal beats decoding it to a string.
            return response;
        });
    };

    reactor.TcpHandle = async (r, conn) =>
    {
        try
        {
            while (true)
            {
                RecvSnapshot snapshot = await conn.ReadAsync();
                while (conn.TryGetItem(snapshot, out SpscRecvRing.Item item))
                {
                    if (item.HasBuffer) conn.ReturnBuffer(in item);
                }

                conn.Write(tcpResponse);
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

using var drain = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
{
    context.Cancel = true;
    Console.WriteLine("[nghttp3-buffered] SIGTERM: draining connections (GOAWAY)...");

    lock (live)
    {
        foreach ((Reactor r, Nghttp3Connection h3) in live)
        {
            r.ScheduleOnReactor(static state => ((Nghttp3Connection)state!).Shutdown(), h3);
        }
        live.Clear();
    }

    Thread.Sleep(2000);
    Console.WriteLine("[nghttp3-buffered] drain complete, exiting");
    Environment.Exit(0);
});

Console.WriteLine($"[nghttp3-buffered] {config.ReactorCount} reactors - tcp :{config.Tcp.Port}, "
                + $"udp :{quicPort} (ngtcp2 {QuicEngine.NativeVersion()})");

foreach (Thread thread in threads)
{
    thread.Join();
}
