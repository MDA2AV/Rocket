using System.Runtime.InteropServices;
using System.Text;
using ioxide;
using ioxide.nghttp3;
using ioxide.ngtcp2;
using ioxide.utils;
using Playground.Shared;

// ─────────────────────────────────────────────────────────────────────────────────────────────
//  nghttp3-buffered - the same HTTP/3 server as Playground/Nghttp3, with the OTHER dispatch mode.
//
//  BUFFERED: dispatch waits for end-of-stream, so the whole body is already in request.Body when
//  your handler runs - no BodyReader, no pacing - and the handler may still await (a PgPool query,
//  Redis, anything ioxide-native resumes inline on the reactor).
//
//  The trade: memory holds the entire body, so this suits normal-sized requests. Use Playground/Nghttp3
//  when uploads can be large or hostile.
//
//      dotnet run -c Release --project Playground/Nghttp3Buffered
//      curl --http3-only -k https://127.0.0.1:8443/plaintext
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
var plaintext = new Nghttp3Response { Body = "Hello, World!"u8.ToArray() };
plaintext.Headers.Add("content-type"u8.ToArray(), "text/plain"u8.ToArray());
plaintext.Headers.Add("server"u8.ToArray(), "ioxide"u8.ToArray());

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
        return h3.RunBufferedAsync(async request =>
        {
            ReadOnlySpan<byte> path = request.Path.Span;

            if (path.SequenceEqual("/plaintext"u8))
            {
                return plaintext;
            }

            if (path.SequenceEqual("/upload"u8))
            {
                // Complete before we run: Length is a property read, the bytes are all here. This
                // is where a real await - storing request.Body, say - would slot in.
                await ValueTask.CompletedTask;
                return Nghttp3Response.Text($"received {request.Body.Length} bytes (buffered) over HTTP/3\n");
            }

            return Nghttp3Response.Text($"hello {Encoding.ASCII.GetString(path)} over HTTP/3 via io_uring\n");
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
