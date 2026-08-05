using System.Text;
using ioxide;
using ioxide.utils;
using Playground.Shared;

// ─────────────────────────────────────────────────────────────────────────────────────────────
//  big - the response WRITE path under a large payload, and the knobs that shape it:
//
//      dotnet run -c Release --project Playground/Tcp/Big
//      curl -s http://127.0.0.1:8080/ | cksum          # same checksum every run, ZC or not
//
//      PLAYGROUND_ZC=1         IORING_OP_SEND_ZC instead of plain SEND. FlushAsync then
//                              completes on the F_NOTIF (kernel released the buffer), so the
//                              slab is never recycled while the kernel still owns it.
//      PLAYGROUND_SLAB=16384   shrink the write slab below the body to force overflow, then
//      PLAYGROUND_OVERFLOW=    pick what overflow does: "grow" doubles the slab in place,
//                              "seg" chains extra segments and sends them as one writev.
//
//  The body is a deterministic pattern (byte i = i % 251 - prime, so not 256-aligned): checksum
//  the output and any one-byte slip between strategies shows up. Needs: ioxide
// ─────────────────────────────────────────────────────────────────────────────────────────────

bool zeroCopy = Env.Flag("PLAYGROUND_ZC");
int slabSize = Env.Int("PLAYGROUND_SLAB", 256 * 1024);
WriteOverflowStrategy overflow = Env.Str("PLAYGROUND_OVERFLOW", "grow") switch
{
    "seg" => WriteOverflowStrategy.Segmented,
    _     => WriteOverflowStrategy.Grow,
};

var config = new ServerConfig
{
    ReactorCount = Env.Int("PLAYGROUND_REACTORS", Environment.ProcessorCount),
    Tcp = new TcpOptions
    {
        Port          = Env.Port("PLAYGROUND_PORT", 8080),
        WriteSlabSize = slabSize,
        WriteOverflow = overflow,
        ZeroCopySend  = zeroCopy,
    },
};

// 100 KB default - well past the small-payload regime the other samples live in.
int bodySize = Env.Int("PLAYGROUND_BODY", 100 * 1024);
byte[] head = Encoding.ASCII.GetBytes(
    $"HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: {bodySize}\r\n\r\n");
byte[] response = new byte[head.Length + bodySize];
head.CopyTo(response, 0);
for (int i = 0; i < bodySize; i++)
{
    response[head.Length + i] = (byte)(i % 251);
}

var threads = new Thread[config.ReactorCount];

for (int i = 0; i < threads.Length; i++)
{
    var reactor = new Reactor(i, config);

    reactor.TcpHandle = async (r, conn) =>
    {
        try
        {
            while (true)
            {
                RecvSnapshot snapshot = await conn.ReadAsync();

                while (conn.TryGetItem(snapshot, out SpscRecvRing.Item item))
                {
                    if (item.HasBuffer)
                    {
                        conn.ReturnBuffer(in item);
                    }
                }

                // With ZC on, this await is the buffer-release notification, not just the
                // submit - which is exactly why the next Write may safely reuse the slab.
                conn.Write(response);
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

Console.WriteLine($"[big] {config.ReactorCount} reactors on :{config.Tcp!.Port}, {bodySize}-byte body, "
                + $"slab={slabSize}, overflow={overflow}, zc={zeroCopy}");

foreach (Thread thread in threads)
{
    thread.Join();
}
