using System.Text;
using System.Text.Json;
using ioxide;
using ioxide.utils;
using Playground.Shared;

// ─────────────────────────────────────────────────────────────────────────────────────────────
//  taskrun - proof that ordinary .NET async works inside a handler. Each request awaits a
//  Task.Run, which is the escape hatch for anything CPU-bound or not ring-native.
//
//  The point: with the per-reactor SynchronizationContext installed, the continuation comes HOME
//  to the reactor, so connection state stays single-threaded and you can keep touching conn after
//  the await. The sample logs once if it ever resumes off-reactor - if that line never prints,
//  the guarantee held.
//
//      dotnet run -c Release --project Playground/Tcp/TaskRun
//      curl http://127.0.0.1:8080/          # -> "hello world"
//
//  Needs: ioxide
// ─────────────────────────────────────────────────────────────────────────────────────────────

// ── Knobs ────────────────────────────────────────────────────────────────────────────────────
// Edit these. That is the whole mechanism - there is no config file and nothing else to find.
// Env.Override exists only so bench/run.sh can drive the sample from outside; delete that line
// when you copy this out and the literals above it are the entire configuration.

ushort port     = 8080;                        // http://127.0.0.1:8080/
int    reactors = Environment.ProcessorCount;  // one ring per reactor, one reactor per core

Env.Override(ref port, ref reactors);
// ─────────────────────────────────────────────────────────────────────────────────────────────

var config = new ServerConfig
{
    ReactorCount = reactors,
    Tcp = new TcpOptions
    {
        Port = port,
    },
};

int offReactorSeen = 0;

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
                    if (item.HasBuffer) conn.ReturnBuffer(in item);
                }

                // Ordinary thread-pool work, awaited from a reactor handler.
                string json = await Task.Run(static () => JsonSerializer.Serialize("hello world"));

                // We should be back on the reactor here. If not, say so once.
                if (!r.OnReactorThread && Interlocked.Exchange(ref offReactorSeen, 1) == 0)
                {
                    Console.WriteLine("[taskrun] continuation resumed OFF the reactor (no sync context)");
                }

                conn.Write("HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 13\r\n\r\n"u8);
                conn.Write(Encoding.UTF8.GetBytes(json));
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

Console.WriteLine($"[taskrun] {config.ReactorCount} reactors on :{config.Tcp.Port} (each request awaits a Task.Run)");

foreach (Thread thread in threads)
{
    thread.Join();
}
