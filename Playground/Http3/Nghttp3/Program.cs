using System.Runtime.InteropServices;
using ioxide;
using ioxide.nghttp3;
using ioxide.ngtcp2;
using ioxide.utils;
using Playground.Shared;

// ─────────────────────────────────────────────────────────────────────────────────────────────
//  nghttp3 - a real HTTP/3 server, whole. ngtcp2 + picotls are bundled as one native library
//  (TLS 1.3 lives inside the transport), and nghttp3 puts HTTP/3 on top. Every reactor binds the
//  UDP port via SO_REUSEPORT and demuxes its own flows.
//
//  STREAMED dispatch: your handler runs at end-of-headers, while the body is still arriving. Each
//  chunk you read credits the peer's flow-control window, so memory is bound by one window rather
//  than by the size of the upload. See Playground/Nghttp3Buffered for the other mode.
//
//      dotnet run -c Release --project Playground/Nghttp3
//      curl --http3-only -k https://127.0.0.1:8443/
//      h2load --alpn-list=h3 -n 1 -c 1 -d bigfile.bin https://127.0.0.1:8443/
//
//  Needs: ioxide, ioxide.ngtcp2, ioxide.nghttp3
// ─────────────────────────────────────────────────────────────────────────────────────────────

// A self-signed localhost cert, unless PLAYGROUND_QUIC_CERT/_KEY point at real ones. Plain X509
// boilerplate - see Playground/Shared/QuicCert.cs.
(string certPath, string keyPath) = QuicCert.Ensure(
    Env.StrOrNull("PLAYGROUND_QUIC_CERT"),
    Env.StrOrNull("PLAYGROUND_QUIC_KEY"));

// One engine for the whole server. ALPN pinned to h3, so nothing else negotiates.
using var engine = new QuicEngine(certPath, keyPath, cidLength: 8, alpn: ["h3"]);

ushort quicPort = Env.Port("PLAYGROUND_QUIC_PORT", 8443);

var config = new ServerConfig
{
    ReactorCount = Env.Int("PLAYGROUND_REACTORS", Environment.ProcessorCount),
    Tcp = new TcpOptions { Port = Env.Port("PLAYGROUND_PORT", 8080) },
    Udp = new UdpOptions { RecvSlots = Env.Int("PLAYGROUND_UDP_SLOTS", 16) },   // multishot recv slots
    Quic = new QuicOptions
    {
        Port = quicPort,
        LocalCidLength = 8,
        ConnectionFactory = engine.CreateFactory(),
    },
};

// PLAYGROUND_QPACK_CAP=4096 advertises a decode-side QPACK dynamic table; 0 is static-only,
// nghttp3's default.
long qpackCapacity = Env.Long("PLAYGROUND_QPACK_CAP", 0);
var h3Options = new Nghttp3Options
{
    QpackDynamicTableCapacity = qpackCapacity,
    QpackBlockedStreams = qpackCapacity > 0 ? 100 : 0,
};

// THE allocation-free pattern: build the response ONCE and reuse the instance for every request.
// Legal because the h3 layer copies status, headers and body into nghttp3 synchronously at submit
// and never retains the object - unlike Nghttp3Response.Text($"..."), which encodes a fresh string
// every time. This is what a hot path should look like.
var response = new Nghttp3Response { Body = "Hello, World!"u8.ToArray() };
response.Headers.Add("content-type"u8.ToArray(), "text/plain"u8.ToArray());
response.Headers.Add("server"u8.ToArray(), "ioxide"u8.ToArray());

// A fixed TCP response for :8080, which still listens alongside the QUIC port.
byte[] tcpResponse = "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 2\r\n\r\nok"u8.ToArray();

// Live connections, so SIGTERM can GOAWAY them all. Each reactor only ever adds its own, but the
// signal handler runs OFF the reactor threads, so a plain lock keeps it honest.
List<(Reactor Reactor, Nghttp3Connection Connection)> live = [];

var threads = new Thread[config.ReactorCount];

for (int i = 0; i < threads.Length; i++)
{
    var reactor = new Reactor(i, config);

    // ---- HTTP/3 on udp :8443 ----------------------------------------------------------------
    // Nghttp3Connection owns the connection's read loop: it feeds stream data into nghttp3,
    // assembles each request - headers, body and all - and calls your function once per request.
    // QPACK, control streams, fin and stream teardown are its problem, not yours.
    reactor.QuicHandle = (r, quicConn) =>
    {
        var h3 = new Nghttp3Connection(quicConn, h3Options);
        lock (live)
        {
            live.Add((r, h3));
        }

        return h3.RunStreamingAsync(async request =>
        {
            // Streamed dispatch, so we are running while the body is still on the wire. Read it to
            // the end: every read credits the peer's flow-control window, which is what throttles a
            // fast sender instead of buffering it. Chunks are valid until the next ReadAsync, and a
            // request with no body simply finds it empty on the first read.
            long total = 0;
            while (true)
            {
                ReadOnlyMemory<byte> chunk = await request.BodyReader!.ReadAsync();
                if (chunk.IsEmpty) break;
                total += chunk.Length;   // a real app would parse or store the chunk here
            }

            // One response object, reused for every request: zero allocations on this path. To
            // route, compare request.Path.Span - it is post-QPACK bytes, so SequenceEqual against a
            // u8 literal beats decoding it to a string.
            return response;
        });
    };

    // ---- plain HTTP/1.1 on tcp :8080 ---------------------------------------------------------
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

// Graceful shutdown. Without this the process dies mid-request and clients see resets. This runs
// OFF the reactor threads, so each Shutdown is marshalled back onto its owning reactor - nghttp3
// and the send path must only be touched there.
using var drain = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
{
    context.Cancel = true;
    Console.WriteLine("[nghttp3] SIGTERM: draining connections (GOAWAY)...");

    lock (live)
    {
        foreach ((Reactor r, Nghttp3Connection h3) in live)
        {
            r.ScheduleOnReactor(static state => ((Nghttp3Connection)state!).Shutdown(), h3);
        }
        live.Clear();
    }

    Thread.Sleep(2000);   // let in-flight requests finish
    Console.WriteLine("[nghttp3] drain complete, exiting");
    Environment.Exit(0);
});

Console.WriteLine($"[nghttp3] {config.ReactorCount} reactors - tcp :{config.Tcp.Port}, "
                + $"udp :{quicPort} (ngtcp2 {QuicEngine.NativeVersion()})");

foreach (Thread thread in threads)
{
    thread.Join();
}
