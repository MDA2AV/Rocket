using System.Text;
using ioxide;
using ioxide.nghttp3;
using ioxide.ngtcp2;
using ioxide.utils;
using Playground.Shared;

// ─────────────────────────────────────────────────────────────────────────────────────────────
//  nghttp3 - a real HTTP/3 server, whole. ngtcp2 + picotls are bundled as one native library (TLS 1.3
//  lives inside the transport), and nghttp3 puts HTTP/3 on top. Every reactor binds the UDP port
//  via SO_REUSEPORT and demuxes its own flows.
//
//  STREAMING dispatch: your handler runs at end-of-headers, while the body is still arriving.
//  Each chunk you read credits the peer's flow-control window, so memory is bound by one window
//  rather than by the size of the upload. Use Playground/Nghttp3Buffered when bodies are small.
//
//      dotnet run -c Release --project Playground/Nghttp3
//      curl --http3-only -k https://127.0.0.1:8443/plaintext
//      h2load --alpn-list=h3 -n 1 -c 1 -d bigfile.bin https://127.0.0.1:8443/upload
//
//  Needs: ioxide, ioxide.ngtcp2, ioxide.nghttp3
// ─────────────────────────────────────────────────────────────────────────────────────────────

// A self-signed localhost cert, unless PLAYGROUND_QUIC_CERT/_KEY point at real ones. Plain X509
// boilerplate - see Playground/Shared/Setup/QuicCert.cs.
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

// Header names/values reused across responses: static byte literals, so a response built only from
// these allocates nothing beyond the response object.
byte[] contentType = "content-type"u8.ToArray();
byte[] textPlain = "text/plain"u8.ToArray();
byte[] setCookie = "set-cookie"u8.ToArray();
byte[] sessionCookie = "session=demo; Path=/; HttpOnly; SameSite=Lax"u8.ToArray();

// THE allocation-free pattern: build the response ONCE and reuse the instance for every request.
// Legal because the h3 layer copies status, headers and body into nghttp3 synchronously at submit
// and never retains the object - unlike Nghttp3Response.Text($"..."), which encodes a fresh string
// every time. This is what a hot path should look like.
var plaintext = new Nghttp3Response { Body = "Hello, World!"u8.ToArray() };
plaintext.Headers.Add(contentType, textPlain);
plaintext.Headers.Add("server"u8.ToArray(), "ioxide"u8.ToArray());

byte[] oneKiB = new byte[1024];
oneKiB.AsSpan().Fill((byte)'x');
oneKiB[^1] = (byte)'\n';

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
            // The request is post-QPACK BYTES throughout (ReadOnlyMemory<byte>). Route by byte
            // compare - no decode, no allocation, no dictionary - and decode only at the edge.
            ReadOnlySpan<byte> path = request.Path.Span;

            if (path.SequenceEqual("/plaintext"u8))
            {
                return plaintext;   // the reused instance: zero allocations per request
            }

            if (path.SequenceEqual("/upload"u8))
            {
                // The handler runs while the body is still arriving. Each read credits the peer's
                // flow-control window, so a slow consumer throttles the sender instead of
                // buffering. Chunks are valid until the next ReadAsync.
                long total = 0;
                while (true)
                {
                    ReadOnlyMemory<byte> chunk = await request.BodyReader!.ReadAsync();
                    if (chunk.IsEmpty) break;
                    total += chunk.Length;   // a real app would parse or store the chunk here
                }

                return Nghttp3Response.Text($"received {total} bytes (streamed) over HTTP/3\n");
            }

            if (path.SequenceEqual("/headers"u8))
            {
                // KeyValueList: ordered, duplicate-preserving, walked over a span. Names are
                // lowercase on the wire (h3 requires it), values are raw octets.
                var report = new StringBuilder($"{request.Headers.Count} header field lines\n");
                foreach ((ReadOnlyMemory<byte> name, ReadOnlyMemory<byte> value) in request.Headers.AsSpan())
                {
                    report.Append($"  {Encoding.ASCII.GetString(name.Span)}: "
                                + $"{Encoding.ASCII.GetString(value.Span)}\n");
                }
                return Nghttp3Response.Text(report.ToString());
            }

            if (path.SequenceEqual("/cookies"u8))
            {
                string session = request.TryGetCookie("session"u8, out ReadOnlyMemory<byte> value)
                    ? Encoding.ASCII.GetString(value.Span)
                    : "(none)";

                // Enumerating walks every cookie field line: h3 may split one logical cookie header
                // across several, which a plain header lookup would miss.
                int count = 0;
                foreach ((ReadOnlyMemory<byte> _, ReadOnlyMemory<byte> _) in request.Cookies)
                {
                    count++;
                }

                var response = new Nghttp3Response
                {
                    Body = Encoding.UTF8.GetBytes($"session={session}, {count} cookie(s) sent\n"),
                };
                response.Headers.Add(contentType, textPlain);
                response.Headers.Add(setCookie, sessionCookie);   // repeat Add for more cookies
                return response;
            }

            if (path.SequenceEqual("/1k"u8))
            {
                var response = new Nghttp3Response { Body = oneKiB };
                response.Headers.Add(contentType, textPlain);
                return response;
            }

            return Nghttp3Response.Text($"hello {Encoding.ASCII.GetString(path)} over HTTP/3 via io_uring\n");
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
using var drain = System.Runtime.InteropServices.PosixSignalRegistration.Create(
    System.Runtime.InteropServices.PosixSignal.SIGTERM, context =>
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
