using System.Security.Cryptography.X509Certificates;
using ioxide;
using ioxide.file;
using ioxide.pg;
using ioxide.redis;
using ioxide.tls;
using Examples.Tls;
using FileExample = Examples.Files.StaticExample;

namespace Examples;

/// <summary>
/// Runs one example handler across a pool of reactors. Pick the mode by arg or EXAMPLES_MODE (the
/// arg wins; default raw-shared). Each mode resolves to a (config, handler, per-reactor service
/// setup) triple.
///
/// Modes:
///   raw-shared | raw-pipes | raw-incremental                  - no backend, fixed plaintext
///   pg-shared | pg-pipes | pg-incremental                     - a query per request, 3 buffer strategies
///   pg-params | pg-rows | pg-error | pg-timeout               - params, row streaming, errors, timeouts
///   redis-shared | redis-cache | redis-types | redis-pipeline - GET, cache-aside, RESP types, pipelining
///   file                                                      - static files off the asset cache
///   tls-ktls | tls-sslstream                                  - TLS: kernel offload vs managed SslStream
///
/// Backends via env: EXAMPLES_PG_* (host/port/user/db/password/pool), EXAMPLES_REDIS_*,
/// EXAMPLES_FILE_DIR, EXAMPLES_TLS_BODY/CERT/KEY. Benchmark numbers live in Examples/RESULTS.md.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        string mode = args.Length > 0
            ? args[0]
            : Environment.GetEnvironmentVariable("EXAMPLES_MODE") ?? "raw-shared";

        Example example = Resolve(mode);

        Console.WriteLine($"[examples] {mode}: {example.Config.ReactorCount} reactors on :{example.Config.Port}");

        var threads = new Thread[example.Config.ReactorCount];
        for (int i = 0; i < threads.Length; i++)
        {
            var reactor = new Reactor(i, example.Config);
            reactor.OnStart = example.OnStart;
            reactor.Handle = example.Handle;

            threads[i] = new Thread(reactor.Run)
            {
                Name = $"reactor-{i}",
                IsBackground = false,
            };
            threads[i].Start();
        }

        foreach (Thread thread in threads)
        {
            thread.Join();
        }

        return 0;
    }

    // One runnable example: the server config, the per-connection handler, and the per-reactor
    // service setup (OnStart) that opens its pool/cache - null when the mode needs no service.
    private readonly record struct Example(
        ServerConfig Config,
        Func<Reactor, Connection, Task> Handle,
        Action<Reactor>? OnStart);

    private static Example Resolve(string mode) => mode switch
    {
        "raw-shared"      => new Example(Configs.Shared,      Raw.SharedExample.Handle,      null),
        "raw-pipes"       => new Example(Configs.Shared,      Raw.PipesExample.Handle,       null),
        "raw-incremental" => new Example(Configs.Incremental, Raw.IncrementalExample.Handle, null),

        "pg-shared"       => WithPg(Configs.Shared,      Pg.SharedExample.Handle),
        "pg-pipes"        => WithPg(Configs.Shared,      Pg.PipesExample.Handle),
        "pg-incremental"  => WithPg(Configs.Incremental, Pg.IncrementalExample.Handle),
        "pg-params"       => WithPg(Configs.Shared,      Pg.ParamsExample.Handle),
        "pg-rows"         => WithPg(Configs.Shared,      Pg.RowsExample.Handle),
        "pg-error"        => WithPg(Configs.Shared,      Pg.ErrorExample.Handle),
        "pg-timeout"      => WithPg(Configs.Shared,      Pg.TimeoutExample.Handle, commandTimeoutMs: 1000),

        "redis-shared"    => WithRedis(Configs.Shared, Redis.SharedExample.Handle),
        "redis-cache"     => WithRedis(Configs.Shared, Redis.CacheExample.Handle),
        "redis-types"     => WithRedis(Configs.Shared, Redis.TypesExample.Handle),
        "redis-pipeline"  => WithRedis(Configs.Shared, Redis.PipelineExample.Handle),

        "file"            => WithFiles(Configs.Shared, FileExample.Handle),

        "tls-ktls"        => WithTls(Configs.Shared, KtlsExample.Handle,      ktls: true),
        "tls-sslstream"   => WithTls(Configs.Shared, SslStreamExample.Handle, ktls: false),

        _ => throw new ArgumentException($"unknown mode '{mode}'"),
    };

    private static Example WithPg(ServerConfig config, Func<Reactor, Connection, Task> handle, int commandTimeoutMs = 30_000)
    {
        var options = new PgOptions
        {
            Host = Environment.GetEnvironmentVariable("EXAMPLES_PG_HOST") ?? "127.0.0.1",
            Port = ushort.TryParse(Environment.GetEnvironmentVariable("EXAMPLES_PG_PORT"), out ushort port) ? port : (ushort)5432,
            User = Environment.GetEnvironmentVariable("EXAMPLES_PG_USER") ?? "bench",
            Database = Environment.GetEnvironmentVariable("EXAMPLES_PG_DB") ?? "bench",
            Password = Environment.GetEnvironmentVariable("EXAMPLES_PG_PASSWORD"),
            PoolSize = int.TryParse(Environment.GetEnvironmentVariable("EXAMPLES_PG_POOL"), out int pool) ? pool : 4,
            CommandTimeoutMs = commandTimeoutMs,
        };

        return new Example(config, handle, r => PgPool.Start(r, options));
    }

    private static Example WithRedis(ServerConfig config, Func<Reactor, Connection, Task> handle)
    {
        var options = new RedisOptions
        {
            Host = Environment.GetEnvironmentVariable("EXAMPLES_REDIS_HOST") ?? "127.0.0.1",
            Port = ushort.TryParse(Environment.GetEnvironmentVariable("EXAMPLES_REDIS_PORT"), out ushort port) ? port : (ushort)6379,
            Password = Environment.GetEnvironmentVariable("EXAMPLES_REDIS_PASSWORD"),
            PoolSize = int.TryParse(Environment.GetEnvironmentVariable("EXAMPLES_REDIS_POOL"), out int pool) ? pool : 4,
        };

        return new Example(config, handle, r => RedisPool.Start(r, options));
    }

    private static Example WithFiles(ServerConfig config, Func<Reactor, Connection, Task> handle)
    {
        string dir = Environment.GetEnvironmentVariable("EXAMPLES_FILE_DIR") ?? EnsureSampleDir();
        var assets = new StaticAssets(dir);
        Console.WriteLine($"[examples] file: {assets.Count} assets under {assets.RootDir}");

        return new Example(config, handle, r =>
        {
            r.AddService(assets);
            AssetReader.CreatePool(r, readers: 4, bufferBytes: 1 << 20);
        });
    }

    private static Example WithTls(ServerConfig config, Func<Reactor, Connection, Task> handle, bool ktls)
    {
        X509Certificate2 cert = TlsCert.EnsureCert();
        SslStreamExample.Init(cert);
        Console.WriteLine($"[examples] tls: {Body.Size}-byte body, cert {TlsCert.CertPath}");

        if (!ktls)
        {
            return new Example(config, handle, null);   // SslStream rides ConnectionStream - no reactor service
        }

        var options = new TlsOptions { CertificatePath = TlsCert.CertPath, KeyPath = TlsCert.KeyPath };
        return new Example(config, handle, r => TlsService.Start(r, options));
    }

    // A tiny sample asset directory so `file` mode has something to serve out of the box.
    private static string EnsureSampleDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ioxide-examples-assets");
        Directory.CreateDirectory(dir);

        string index = Path.Combine(dir, "index.html");
        if (!System.IO.File.Exists(index))
        {
            System.IO.File.WriteAllText(index, "<!doctype html><h1>ioxide</h1><p>static files served off the ring.</p>");
        }

        return dir;
    }
}
