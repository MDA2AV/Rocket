using System.Text;
using ioxide;
using ioxide.http3;
using ioxide.ngtcp2;
using ioxide.utils;
using Playground.Shared;

// ─────────────────────────────────────────────────────────────────────────────────────────────
//  http3 - HTTP/3 with NO native h3 code. ioxide.http3 is a pure-C# stack: frame parsing, QPACK
//  (static table + Huffman) and request dispatch, all managed. ngtcp2 still provides the QUIC
//  transport underneath, but nothing above it is native.
//
//  This project deliberately does NOT reference ioxide.nghttp3 - check the publish output and you
//  will find libioxide_ngtcp2.so and no nghttp3 at all. That is the whole point of reading it next
//  to Playground/H3, which is the same surface backed by the native library.
//
//  It rides any QuicConnection via its stream read surface, so it is engine-agnostic.
//
//      dotnet run -c Release --project Playground/Http3
//      curl --http3-only -k https://127.0.0.1:8443/
//
//  Needs: ioxide, ioxide.ngtcp2, ioxide.http3
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

byte[] tcpResponse = "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 2\r\n\r\nok"u8.ToArray();

var threads = new Thread[config.ReactorCount];

for (int i = 0; i < threads.Length; i++)
{
    var reactor = new Reactor(i, config);

    // Http3Connection owns the connection's read loop and calls your function once per request -
    // the same shape as Nghttp3Connection, with a managed implementation behind it. There is no
    // GOAWAY registry here because there is no nghttp3 session to drain.
    reactor.QuicHandle = (r, quicConn) => new Http3Connection(quicConn).RunAsync(async request =>
    {
        if (request.Path.Span.SequenceEqual("/upload"u8))
        {
            // Streamed body under flow-control pacing, exactly as in the nghttp3 sample: memory is
            // bound by one window rather than by the size of the upload.
            long total = 0;
            while (true)
            {
                ReadOnlyMemory<byte> chunk = await request.BodyReader!.ReadAsync();
                if (chunk.IsEmpty) break;
                total += chunk.Length;
            }
            return Http3Response.Text($"received {total} bytes over pure-C# HTTP/3\n");
        }

        return Http3Response.Text(
            $"hello {Encoding.ASCII.GetString(request.Path.Span)} over pure-C# HTTP/3\n");
    });

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

Console.WriteLine($"[http3] {config.ReactorCount} reactors - tcp :{config.Tcp.Port}, "
                + $"udp :{quicPort} (pure-C# h3 over ngtcp2 {QuicEngine.NativeVersion()})");

foreach (Thread thread in threads)
{
    thread.Join();
}
