using System.IO.Pipelines;
using ioxide;
using ioxide.utils;
using Playground.Shared;

// ─────────────────────────────────────────────────────────────────────────────────────────────
//  pipe - the same workload as Playground/Raw, but read and written through the System.IO.Pipelines
//  adapters instead of the raw connection API. If your code already speaks PipeReader/PipeWriter,
//  this is the shape to copy; run it against Raw to price what the adapter costs.
//
//  The reader owns the carry: unconsumed bytes are held across reads with no copy and no slab, and
//  buffers return to the ring automatically once fully consumed.
//
//      dotnet run -c Release --project Playground/Pipe
//      curl http://127.0.0.1:8080/
//
//  Needs: ioxide
// ─────────────────────────────────────────────────────────────────────────────────────────────

var config = new ServerConfig
{
    ReactorCount = Env.Int("PLAYGROUND_REACTORS", Environment.ProcessorCount),
    Tcp = new TcpOptions
    {
        Port = Env.Port("PLAYGROUND_PORT", 8080),
    },
};

byte[] response = "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 2\r\n\r\nok"u8.ToArray();

var threads = new Thread[config.ReactorCount];

for (int i = 0; i < threads.Length; i++)
{
    var reactor = new Reactor(i, config);

    reactor.TcpHandle = async (r, conn) =>
    {
        var reader = new TcpConnectionPipeReader(conn);
        var writer = new TcpConnectionPipeWriter(conn);

        try
        {
            while (true)
            {
                // io_uring recv behind the PipeReader surface - still resumes inline on the reactor.
                ReadResult result = await reader.ReadAsync();

                // This sample doesn't parse the request either: consume everything. A real handler
                // would walk result.Buffer for complete requests and AdvanceTo(consumed, examined)
                // so a partial one stays buffered for the next read.
                reader.AdvanceTo(result.Buffer.End);

                response.CopyTo(writer.GetSpan(response.Length));
                writer.Advance(response.Length);
                await writer.FlushAsync();

                if (result.IsCompleted) return;
            }
        }
        finally
        {
            reader.Complete();
            conn.DecRef();
        }
    };

    threads[i] = new Thread(reactor.Run) { Name = $"reactor-{i}" };
    threads[i].Start();
}

Console.WriteLine($"[pipe] {config.ReactorCount} reactors on :{config.Tcp.Port}");

foreach (Thread thread in threads)
{
    thread.Join();
}
