using Examples.PipeReader;
using Examples.Stream;
using Examples.ZeroAlloc.Basic;
using Examples.ZeroAlloc.SqPoll;
using zerg;
using zerg.Engine;
using zerg.Engine.Configs;

namespace Examples;

// dotnet publish -f net10.0 -c Release /p:PublishAot=true /p:OptimizationPreference=Speed

internal class Program
{
    public static async Task Main(string[] args)
    {
        var mode = args.Length > 0 ? args[0] : "pipereader";
        var reactorCount = args.Length > 1 && int.TryParse(args[1], out int rc) ? rc : 12;

        // SQPOLL mode uses a custom engine with SQPOLL-enabled rings
        var engine = mode == "sqpoll"
            ? SqPollExample.CreateEngine(reactorCount: reactorCount)
            : new Engine(new EngineOptions
            {
                Ip = "0.0.0.0",
                Port = 8080,
                Backlog = 65535,
                ReactorCount = reactorCount,
                AcceptorConfig = new AcceptorConfig(
                    RingFlags: 0,
                    SqCpuThread: -1,
                    SqThreadIdleMs: 100,
                    RingEntries: 8 * 1024,
                    BatchSqes: 4096,
                    CqTimeout: 100_000_000,
                    IPVersion: IPVersion.IPv6DualStack
                ),
                ReactorConfigs = Enumerable.Range(0, reactorCount).Select(_ => new ReactorConfig(
                    RingFlags: (1u << 12) | (1u << 13), // SINGLE_ISSUER | DEFER_TASKRUN
                    SqCpuThread: -1,
                    SqThreadIdleMs: 100,
                    RingEntries: 8 * 1024,
                    RecvBufferSize: 32 * 1024,
                    BufferRingEntries: 16 * 1024,
                    BatchCqes: 4096,
                    MaxConnectionsPerReactor: 8 * 1024,
                    CqTimeout: 1_000_000
                )).ToArray()
            });

        engine.Listen();

        var cts = new CancellationTokenSource();
        _ = Task.Run(() => {
            Console.ReadLine();
            engine.Stop();
            cts.Cancel();
        }, cts.Token);

        // Pick the handler:
        //   "raw"        — zero-copy, manual ring management (fastest)
        //   "sqpoll"     — same as raw but with SQPOLL-enabled rings
        //   "pipereader" — zero-copy via PipeReader adapter
        //   "stream"     — copy-per-read via Stream adapter
        Func<Connection, Task> handler = mode switch
        {
            "raw"        => Rings_as_ReadOnlySpan.HandleConnectionAsync,
            "sqpoll"     => SqPollExample.HandleConnectionAsync,
            "pipereader" => PipeReaderExample.HandleConnectionAsync,
            "stream"     => StreamExample.HandleConnectionAsync,
            _            => PipeReaderExample.HandleConnectionAsync,
        };

        Console.WriteLine($"Running with handler: {mode}");

        try
        {
            // Loop to handle new connections, fire and forget approach
            while (engine.ServerRunning)
            {
                var connection = await engine.AcceptAsync(cts.Token);
                if (connection is null) continue;
                _ = handler(connection);
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Signaled to stop");
        }

        cts.Dispose();
        Console.WriteLine("Main loop finished.");
    }
}
