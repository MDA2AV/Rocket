using ioxide;
using ioxide.pg;

namespace Examples;

/// <summary>
/// Runs the landing-page examples, grouped in two categories:
///
///   pg  - every request runs a query through a per-reactor PgPool
///         (needs a trust-auth Postgres, user/db "bench"; override via EXAMPLES_PG_*)
///   raw - no database: read, return buffers, answer fixed plaintext
///
///   dotnet run --project Examples -- pg-pipes | pg-shared | pg-incremental
///   dotnet run --project Examples -- raw-pipes | raw-shared | raw-incremental
///
/// Benchmark numbers live in Examples/RESULTS.md.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0] : "pg-pipes";

        (ServerConfig config, Func<Reactor, Connection, Task> handle, bool needsPg) = mode switch
        {
            "pg-pipes"        => (Configs.Shared,      Pg.PipesExample.Handle,        true),
            "pg-shared"       => (Configs.Shared,      Pg.SharedExample.Handle,       true),
            "pg-incremental"  => (Configs.Incremental, (Func<Reactor, Connection, Task>)Pg.IncrementalExample.Handle, true),
            "raw-pipes"       => (Configs.Shared,      Raw.PipesExample.Handle,       false),
            "raw-shared"      => (Configs.Shared,      Raw.SharedExample.Handle,      false),
            "raw-incremental" => (Configs.Incremental, Raw.IncrementalExample.Handle, false),
            _ => throw new ArgumentException($"unknown mode '{mode}'"),
        };

        var pgOptions = new PgOptions
        {
            Host = Environment.GetEnvironmentVariable("EXAMPLES_PG_HOST") ?? "127.0.0.1",
            Port = ushort.TryParse(Environment.GetEnvironmentVariable("EXAMPLES_PG_PORT"), out ushort p) ? p : (ushort)5432,
            User = Environment.GetEnvironmentVariable("EXAMPLES_PG_USER") ?? "bench",
            Database = Environment.GetEnvironmentVariable("EXAMPLES_PG_DB") ?? "bench",
            PoolSize = int.TryParse(Environment.GetEnvironmentVariable("EXAMPLES_PG_POOL"), out int ps) ? ps : 4,
        };

        Console.WriteLine($"[examples] {mode}: {config.ReactorCount} reactors on :{config.Port}");

        var threads = new Thread[config.ReactorCount];

        for (int i = 0; i < config.ReactorCount; i++)
        {
            var reactor = new Reactor(i, config);
            if (needsPg)
            {
                reactor.OnStart = r => PgPool.Start(r, pgOptions);
            }
            reactor.Handle = handle;

            threads[i] = new Thread(reactor.Run) { Name = $"reactor-{i}", IsBackground = false };
            threads[i].Start();
        }

        foreach (var t in threads) t.Join();
        return 0;
    }
}
