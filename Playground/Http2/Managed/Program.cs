using System.Text;
using ioxide;
using ioxide.http2;
using Playground.Shared;

// ─────────────────────────────────────────────────────────────────────────────────────────────
//  http2 - an HTTP/2 server in pure C#: framing, HPACK and flow control; ioxide owns the
//  ring, the loop and the connection, so a response is written straight into the write slab.
//
//      dotnet run -c Release --project Playground/Http2/Managed
//      curl --http2-prior-knowledge http://127.0.0.1:8080/hello
//
//  This is h2c with PRIOR KNOWLEDGE: the peer opens with the HTTP/2 connection preface and no
//  upgrade dance. For h2 over TLS, terminate with ioxide's TlsService (ALPN "h2") and feed the
//  decrypted bytes in the same way. Needs: ioxide, ioxide.http2
// ─────────────────────────────────────────────────────────────────────────────────────────────

var config = new ServerConfig
{
    ReactorCount   = Env.Int("PLAYGROUND_REACTORS", Environment.ProcessorCount),  // io_uring rings/threads - one per core
    RingEntries    = 8192,                                                        // SQ/CQ depth per ring
    DualStack      = false,                                                       // true = one IPv6 socket also accepts IPv4-mapped
    RecvBufferSize = 32 * 1024,                                                   // bytes per shared recv buffer
    RecvSlots      = 4096,                                                        // shared recv buffer-ring depth
    Incremental    = null,                                                        // per-connection recv rings (6.12+) - see Tcp/Incremental
    Udp            = null,                                                        // no raw UDP sockets (TCP-only server)
    Quic           = null,                                                        // no QUIC transport - see Http3/* and Quic/Alpn
    Tcp = new TcpOptions
    {
        Port             = Env.Port("PLAYGROUND_PORT", 8080),
        ExtraPorts       = [],                             // extra listener ports (one handler, several doors)
        ListenBacklog    = 1024,                           // accept-queue depth per SO_REUSEPORT listener
        WriteSlabSize    = 16 * 1024,                      // per-connection write buffer before overflow kicks in
        PoolMax          = 1024,                           // pooled connection objects kept per reactor
        WriteOverflow    = WriteOverflowStrategy.Grow,     // Grow = realloc one slab; Segmented = chain + vectored SENDMSG
        ZeroCopySend     = false,                          // SEND_ZC: kernel copies less, wins on large writes
        RecvQueueEntries = 64,                             // per-connection recv completion queue depth
    },
};

int bodyBytes = Env.Int("PLAYGROUND_BODY", 2);
byte[] body = bodyBytes == 2
    ? "ok"u8.ToArray()
    : [.. Enumerable.Repeat((byte)'x', bodyBytes)];

var threads = new Thread[config.ReactorCount];

for (int i = 0; i < threads.Length; i++)
{
    var reactor = new Reactor(i, config);

    reactor.TcpHandle = async (r, conn) =>
    {
        try
        {
            // The connection owns the read loop from here: it feeds nghttp2, dispatches each
            // request once its stream ends, and drains the egress once per batch.
            await new Http2Connection(conn).RunBufferedAsync(_ => new Http2Response
            {
                Status = 200,
                Body = body,
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

Console.WriteLine($"[http2] {config.ReactorCount} reactors on :{config.Tcp!.Port}, "
                + $"{body.Length}-byte body (h2c prior knowledge)");

foreach (Thread thread in threads)
{
    thread.Join();
}
