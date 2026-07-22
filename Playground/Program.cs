using System.Runtime.InteropServices;
using ioxide;
using ioxide.file;
using ioxide.pg;

namespace Playground;

/// <summary>
/// A host for the ioxide engine. PLAYGROUND_MODE picks the handler: raw (plaintext "ok"),
/// pg (a PgPool per reactor), file (static files over the asset cache), hop (raw via the thread
/// pool). Each mode registers its services from OnStart; handlers fetch them with GetService.
/// </summary>
internal static class Program
{
    private static int Main()
    {
        var config = new ServerConfig
        {
            Port = 8080,
            ReactorCount = int.TryParse(Environment.GetEnvironmentVariable("PLAYGROUND_REACTORS"), out int reactors) ? reactors : 12,
            Incremental = Environment.GetEnvironmentVariable("PLAYGROUND_INCREMENTAL") == "1"
        };

        string mode = Environment.GetEnvironmentVariable("PLAYGROUND_MODE") ?? "raw";
        string assetDir = Environment.GetEnvironmentVariable("PLAYGROUND_DIR") ?? "/tmp/ioxide-assets";

        var pgOptions = new PgOptions
        {
            Host = Environment.GetEnvironmentVariable("PLAYGROUND_PG_HOST") ?? "127.0.0.1",
            Port = ushort.TryParse(Environment.GetEnvironmentVariable("PLAYGROUND_PG_PORT"), out ushort pgPort) ? pgPort : (ushort)5432,
            User = Environment.GetEnvironmentVariable("PLAYGROUND_PG_USER") ?? "bench",
            Database = Environment.GetEnvironmentVariable("PLAYGROUND_PG_DB") ?? "bench",
            PoolSize = int.TryParse(Environment.GetEnvironmentVariable("PLAYGROUND_PG_POOL"), out int poolSize) ? poolSize : 4,
            CommandTimeoutMs = int.TryParse(Environment.GetEnvironmentVariable("PLAYGROUND_PG_TIMEOUT"), out int pgTimeout) ? pgTimeout : 30_000,
        };

        // The asset cache opens every file once and is shared across all reactors (its descriptors
        // are stable and read positionally). StaticAssets wraps it so it can be reloaded atomically.
        StaticAssets? assets = null;
        if (mode == "file")
        {
            // PLAYGROUND_CACHE_MAX: per-file byte ceiling for pinning bodies in memory
            // (0 forces every request through the ring-read path; default 256KB).
            int cacheMax = int.TryParse(Environment.GetEnvironmentVariable("PLAYGROUND_CACHE_MAX"), out int max)
                ? max
                : AssetCache.DefaultMaxCachedFileBytes;

            Handlers.EnsureSampleDir(assetDir);
            assets = new StaticAssets(assetDir, cacheMax);
            Console.WriteLine($"[playground] asset cache: {assets.Count} files under {assets.RootDir} (pin ≤ {cacheMax}B)");
        }

        // Reload the assets on SIGHUP (e.g. `kill -HUP <pid>` after a deploy): a fresh snapshot is
        // opened and swapped in atomically; the old descriptors close after a grace.
        using IDisposable? reload = assets is null ? null : PosixSignalRegistration.Create(PosixSignal.SIGHUP, ctx =>
        {
            ctx.Cancel = true;   // handle it; don't let the default action terminate us
            assets.Reload();
            Console.WriteLine($"[playground] reloaded - now serving {assets.Count} files");
        });
        
        Console.WriteLine($"[playground] {config.ReactorCount} reactors on :{config.Port} (mode={mode})");

        var threads = new Thread[config.ReactorCount];

        for (int i = 0; i < config.ReactorCount; i++)
        {
            var reactor = new Reactor(i, config);

            // Pick the handler, and register whatever per-reactor services it needs. OnStart runs
            // on the reactor thread, so every client opened there rides that reactor's ring.
            switch (mode)
            {
                case "pg":
                    reactor.Handle = Handlers.Pg;
                    reactor.OnStart = r => PgPool.Start(r, pgOptions);
                    break;

                case "file":
                    reactor.Handle = Handlers.File;
                    reactor.OnStart = r =>
                    {
                        r.AddService(assets!);
                        AssetReader.CreatePool(r, readers: 4, bufferBytes: 1 << 20);
                    };
                    break;

                case "pipe":
                    reactor.Handle = Handlers.Pipe;
                    break;

                case "hop":
                    reactor.Handle = Handlers.Hop;
                    break;

                case "taskrun":
                    reactor.Handle = Handlers.TaskRun;
                    break;

                default:
                    reactor.Handle = Handlers.Raw;
                    break;
            }

            threads[i] = new Thread(reactor.Run)
            {
                Name = $"reactor-{i}",
                IsBackground = false
            };

            threads[i].Start();
        }

        foreach (var thread in threads)
        {
            thread.Join();
        }

        return 0;
    }
}
