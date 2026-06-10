using System.Runtime.InteropServices;
using ioxide;
using ioxide.file;
using ioxide.pg;

namespace Playground;

/// <summary>
/// A host for the ioxide engine. It spins up one reactor per slot and wires each with one of three
/// request handlers, chosen by the PLAYGROUND_MODE environment variable:
///
///   raw   (default) — serve a fixed plaintext "ok".
///   pg              — open a Postgres connection per reactor; answer with "SELECT 42".
///   file            — open a file per reactor and serve it, read off the ring (the Fractal model).
/// </summary>
internal static class Program
{
    private static int Main()
    {
        var config = new ServerConfig
        {
            Port = 8080,
            UsePipe = false,
            ReactorCount = 2
        };

        string mode = Environment.GetEnvironmentVariable("PLAYGROUND_MODE") ?? "raw";
        string assetDir = Environment.GetEnvironmentVariable("PLAYGROUND_DIR") ?? "/tmp/ioxide-assets";

        // The asset cache opens every file once and is shared across all reactors (its descriptors
        // are stable and read positionally). StaticAssets wraps it so it can be reloaded atomically.
        StaticAssets? assets = null;
        if (mode == "file")
        {
            Handlers.EnsureSampleDir(assetDir);
            assets = new StaticAssets(assetDir);
            Console.WriteLine($"[playground] asset cache: {assets.Count} files under {assets.RootDir}");
        }

        // Reload the assets on SIGHUP (e.g. `kill -HUP <pid>` after a deploy): a fresh snapshot is
        // opened and swapped in atomically; the old descriptors close after a grace.
        using IDisposable? reload = assets is null ? null : PosixSignalRegistration.Create(PosixSignal.SIGHUP, ctx =>
        {
            ctx.Cancel = true;   // handle it; don't let the default action terminate us
            assets.Reload();
            Console.WriteLine($"[playground] reloaded — now serving {assets.Count} files");
        });

        Console.WriteLine($"[playground] {config.ReactorCount} reactors on :{config.Port} (mode={mode})");

        var threads = new Thread[config.ReactorCount];

        for (int i = 0; i < config.ReactorCount; i++)
        {
            var reactor = new Reactor(i, config);

            // Pick the handler, and open whatever per-reactor client it needs (on the reactor thread).
            switch (mode)
            {
                case "pg":
                    reactor.Handle = Handlers.Pg;
                    reactor.OnStart = r => r.State = PgConnection.Connect(r, "bench", "bench");
                    break;

                case "file":
                    reactor.Handle = Handlers.File;
                    reactor.OnStart = r => r.State = FileEndpoint.Open(r, assets!);
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
