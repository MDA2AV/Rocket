using ioxide;
using ioxide.utils;
using Playground.Shared;

// ─────────────────────────────────────────────────────────────────────────────────────────────
//  hop - Playground/Raw, except every request deliberately bounces off the reactor and back.
//  Task.Yield() sends the continuation to the thread pool; the reactor's SynchronizationContext
//  then posts it home, which wakes the reactor through its eventfd.
//
//  This is the sample to read if you need to call something that ISN'T ring-native - a blocking
//  library, a CPU-bound step - and want to see what leaving the reactor costs. Run it against Raw.
//
//      dotnet run -c Release --project Playground/Hop
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
        try
        {
            while (true)
            {
                RecvSnapshot snapshot = await conn.ReadAsync();

                // Off to the thread pool and back. Everything after this line runs on the reactor
                // again, because the per-reactor SynchronizationContext posts it home - which is
                // what makes it safe to touch the connection below.
                await Task.Yield();

                while (conn.TryGetItem(snapshot, out SpscRecvRing.Item item))
                {
                    if (item.HasBuffer) conn.ReturnBuffer(in item);
                }

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

Console.WriteLine($"[hop] {config.ReactorCount} reactors on :{config.Tcp.Port} (every request bounces off-reactor)");

foreach (Thread thread in threads)
{
    thread.Join();
}
