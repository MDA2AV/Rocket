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
//  The body is a deterministic pattern (byte i = i % 251 - prime, so not 256-aligned): checksum
//  the output and any one-byte slip between strategies shows up. Needs: ioxide
// ─────────────────────────────────────────────────────────────────────────────────────────────

// ── Knobs ────────────────────────────────────────────────────────────────────────────────────
// Edit these. That is the whole mechanism - there is no config file and nothing else to find.
// Env.Override exists only so bench/run.sh can drive the sample from outside; delete that line
// when you copy this out and the literals above it are the entire configuration.

ushort port      = 8080;                        // http://127.0.0.1:8080/
int    reactors  = Environment.ProcessorCount;
int    bodyBytes = 100 * 1024;                  // well past the small-payload regime

Env.Override(ref port, ref reactors, ref bodyBytes);

// IORING_OP_SEND_ZC instead of plain SEND. FlushAsync then completes on the F_NOTIF (the kernel
// released the buffer), so the slab is never recycled while the kernel still owns it. It only
// pays off once the body is large enough that pinning beats copying.
bool zeroCopy = false;

// Drop the slab below bodyBytes (try 16 * 1024) to force the overflow path every request.
int slabSize = 256 * 1024;

// What overflow does: Grow reallocates the slab in place, Segmented chains extra segments and
// sends them as one SENDMSG. Same bytes on the wire either way - checksum both and see.
WriteOverflowStrategy overflow = WriteOverflowStrategy.Grow;
// ─────────────────────────────────────────────────────────────────────────────────────────────

var config = new ServerConfig
{
    ReactorCount = reactors,
    Tcp = new TcpOptions
    {
        Port          = port,
        WriteSlabSize = slabSize,
        WriteOverflow = overflow,
        ZeroCopySend  = zeroCopy,
    },
};

int bodySize = bodyBytes;
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
