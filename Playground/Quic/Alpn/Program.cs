using System.Buffers;
using System.Text;
using ioxide;
using ioxide.nghttp3;
using ioxide.ngtcp2;
using Playground.Shared;

// ─────────────────────────────────────────────────────────────────────────────────────────────
//  quic-alpn - one QUIC listener, two protocols, picked by ALPN: connections that negotiated
//  "h3" get real HTTP/3 through ioxide.nghttp3, anything else (including no ALPN at all) gets a
//  raw stream echo through the QuicConnectionDualPipe adapters - QUIC's twin of Tcp.Pipe.
//
//  This server is also QUIC-ONLY: ServerConfig.Tcp is null, so no TCP port is bound at all.
//
//      dotnet run -c Release --project Playground/Quic/Alpn
//      curl --http3-only -ks https://127.0.0.1:8443/          # the h3 branch
//
//  The QUIC handler launches before the handshake, so the ALPN is not known at entry. The demux
//  below awaits the first read WITHOUT draining it - 1-RTT stream data implies the handshake
//  finished - peeks NegotiatedProtocol, and hands the still-queued items to whichever layer owns
//  the connection from here. Both start by reading the same queue, so nothing is lost.
//  Needs: ioxide.ngtcp2, ioxide.nghttp3
// ─────────────────────────────────────────────────────────────────────────────────────────────

(string certPath, string keyPath) = QuicCert.Ensure(
    Env.StrOrNull("PLAYGROUND_QUIC_CERT"),
    Env.StrOrNull("PLAYGROUND_QUIC_KEY"));

// Permissive ALPN (no allowlist): h3 clients negotiate "h3"; everything else still handshakes
// and falls through to the echo branch.
using var engine = new QuicEngine(certPath, keyPath, cidLength: 8);

var config = new ServerConfig
{
    ReactorCount = Env.Int("PLAYGROUND_REACTORS", Environment.ProcessorCount),
    Tcp = null,   // QUIC-only: no TCP listener is bound
    Udp = new UdpOptions { RecvSlots = Env.Int("PLAYGROUND_UDP_SLOTS", 16) },
    Quic = new QuicOptions
    {
        Port = Env.Port("PLAYGROUND_QUIC_PORT", 8443),
        LocalCidLength = 8,
        ConnectionFactory = engine.CreateFactory(),
    },
};

var threads = new Thread[config.ReactorCount];

for (int i = 0; i < threads.Length; i++)
{
    var reactor = new Reactor(i, config);

    reactor.QuicHandle = async (r, conn) =>
    {
        // Peek only - the items stay queued for whichever branch takes over.
        await conn.ReadAsync();

        if (conn.NegotiatedProtocol == "h3")
        {
            // Owns the handler ref (DecRef on exit). Routes by byte compare - no per-request
            // strings on the hot path.
            await new Nghttp3Connection(conn).RunBufferedAsync(
                static request => request.Path.Span.SequenceEqual("/plaintext"u8)
                    ? Nghttp3Response.Text("Hello, World!")
                    : Nghttp3Response.Text(
                        $"hello {Encoding.ASCII.GetString(request.Path.Span)} over HTTP/3 via io_uring\n"));
            return;
        }

        await PipeEcho(conn);
    };

    threads[i] = new Thread(reactor.Run) { Name = $"reactor-{i}" };
    threads[i].Start();
}

Console.WriteLine($"[quic-alpn] {config.ReactorCount} reactors, quic-only on udp :{config.Quic!.Port} "
                + $"(h3 -> HTTP/3, other ALPN -> stream echo)");

foreach (Thread thread in threads)
{
    thread.Join();
}

// Raw stream echo over the dual pipe: auto-binds to the client's first stream, echoes until the
// peer's fin, then Complete() half-closes with our own fin. A connection closed before the
// handshake falls through with a completed reader and exits clean.
static async Task PipeEcho(QuicConnection conn)
{
    try
    {
        var pipe = new QuicConnectionDualPipe(conn);
        while (true)
        {
            var result = await pipe.Input.ReadAsync();
            foreach (ReadOnlyMemory<byte> segment in result.Buffer)
            {
                pipe.Output.Write(segment.Span);
            }
            await pipe.Output.FlushAsync();
            pipe.Input.AdvanceTo(result.Buffer.End);

            if (result.IsCompleted) break;
        }
        pipe.Output.Complete();
        pipe.Input.Complete();
    }
    finally
    {
        conn.DecRef();
    }
}
