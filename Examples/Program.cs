using System.Security.Cryptography.X509Certificates;
using ioxide;
using ioxide.file;
using ioxide.pg;
using ioxide.ngtcp2;
using ioxide.redis;
using ioxide.tls;
using Examples.Quic;
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
///   quic-h3                                                   - QUIC on UDP :8443, demuxed by ALPN:
///                                                               HTTP/3 (ioxide.nghttp3) for "h3", dual-pipe
///                                                               stream echo for anything else
///
/// Backends via env: EXAMPLES_PG_* (host/port/user/db/password/pool), EXAMPLES_REDIS_*,
/// EXAMPLES_FILE_DIR, EXAMPLES_TLS_BODY/CERT/KEY, EXAMPLES_QUIC_PORT/CERT/KEY.
/// Benchmark numbers live in Examples/RESULTS.md.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        string mode = args.Length > 0
            ? args[0]
            : Environment.GetEnvironmentVariable("EXAMPLES_MODE") ?? "raw-shared";

        Example example = Resolve(mode);

        Console.WriteLine($"[examples] {mode}: {example.Config.ReactorCount} reactors on :{example.Config.Tcp.Port}");

        var threads = new Thread[example.Config.ReactorCount];
        for (int i = 0; i < threads.Length; i++)
        {
            var reactor = new Reactor(i, example.Config);
            reactor.OnStart = example.OnStart;
            reactor.TcpHandle = example.Handle;
            reactor.QuicHandle = example.QuicHandle;

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
    // QuicHandle is set only by the quic-h3 mode (the config then carries the UDP listener).
    private readonly record struct Example(
        ServerConfig Config,
        Func<Reactor, TcpConnection, Task> Handle,
        Action<Reactor>? OnStart,
        Func<Reactor, QuicConnection, Task>? QuicHandle = null);

    private static Example Resolve(string mode) => mode switch
    {
        "raw-shared"      => new Example(Configs.Shared,      Raw.SharedExample.Handle,      null),
        "raw-pipes"       => new Example(Configs.Shared,      Raw.PipesExample.Handle,       null),
        "raw-incremental" => new Example(Configs.Incremental, Raw.IncrementalExample.Handle, null),

        // Large-body plaintext, to exercise the response send path with big payloads. raw-zc flips on
        // IORING_OP_SEND_ZC (TcpOptions.ZeroCopySend); raw-big is the plain-SEND baseline.
        "raw-big"         => new Example(Configs.Shared with { Tcp = Configs.Shared.Tcp with { WriteSlabSize = 256 * 1024 } },                      Raw.BigExample.Handle, null),
        "raw-zc"          => new Example(Configs.Shared with { Tcp = Configs.Shared.Tcp with { WriteSlabSize = 256 * 1024, ZeroCopySend = true } }, Raw.BigExample.Handle, null),

        // Grow vs Segmented head-to-head: the 100KB BigExample body forced onto a 16KB slab so it overflows.
        "raw-big-grow"    => new Example(Configs.Shared with { Tcp = Configs.Shared.Tcp with { WriteSlabSize = 16 * 1024, WriteOverflow = WriteOverflowStrategy.Grow } },      Raw.BigExample.Handle, null),
        "raw-big-seg"     => new Example(Configs.Shared with { Tcp = Configs.Shared.Tcp with { WriteSlabSize = 16 * 1024, WriteOverflow = WriteOverflowStrategy.Segmented } }, Raw.BigExample.Handle, null),

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

        "quic-h3"         => WithQuicH3(Configs.Shared),

        _ => throw new ArgumentException($"unknown mode '{mode}'"),
    };

    private static Example WithPg(ServerConfig config, Func<Reactor, TcpConnection, Task> handle, int commandTimeoutMs = 30_000)
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

    private static Example WithRedis(ServerConfig config, Func<Reactor, TcpConnection, Task> handle)
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

    private static Example WithFiles(ServerConfig config, Func<Reactor, TcpConnection, Task> handle)
    {
        string dir = Environment.GetEnvironmentVariable("EXAMPLES_FILE_DIR") ?? DefaultAssetDir();
        var assets = new StaticAssets(dir);
        Console.WriteLine($"[examples] file: {assets.Count} assets under {assets.RootDir}");

        return new Example(config, handle, r =>
        {
            r.AddService(assets);
            AssetReader.CreatePool(r, readers: 4, bufferBytes: 1 << 20);
        });
    }

    // Rooted for the process lifetime - the engine backs every reactor's QUIC connections.
    private static QuicEngine? _quicEngine;

    private static Example WithQuicH3(ServerConfig config)
    {
        (string certPath, string keyPath) = QuicH3Example.EnsureQuicCert();

        // Permissive ALPN (no allowlist): h3 clients negotiate "h3" and get HTTP/3; anything
        // else - including clients that offer no ALPN at all - falls through to the pipe echo.
        _quicEngine = new QuicEngine(certPath, keyPath, cidLength: 8);

        ushort quicPort = ushort.TryParse(Environment.GetEnvironmentVariable("EXAMPLES_QUIC_PORT"), out ushort qp) ? qp : (ushort)8443;
        var quicOptions = new QuicOptions
        {
            Port = quicPort,
            LocalCidLength = 8,
            ConnectionFactory = _quicEngine.CreateFactory(),
        };

        Console.WriteLine($"[examples] quic-h3 on udp :{quicPort} (ngtcp2 {QuicEngine.NativeVersion()}) - " +
                          $"try: curl --http3-only -k https://127.0.0.1:{quicPort}/hello");

        // :8080 still serves plaintext TCP (no TCP opt-out yet); the QUIC listener rides alongside.
        return new Example(
            config with { Quic = quicOptions },
            Raw.SharedExample.Handle,
            null,
            QuicH3Example.Handle);
    }

    private static Example WithTls(ServerConfig config, Func<Reactor, TcpConnection, Task> handle, bool ktls)
    {
        X509Certificate2 cert = TlsCert.EnsureCert();
        SslStreamExample.Init(cert);
        Console.WriteLine($"[examples] tls: {Body.Size}-byte body, cert {TlsCert.CertPath}");

        if (!ktls)
        {
            return new Example(config, handle, null);   // SslStream rides TcpConnectionStream - no reactor service
        }

        var options = new TlsOptions { CertificatePath = TlsCert.CertPath, KeyPath = TlsCert.KeyPath };
        return new Example(config, handle, r => TlsService.Start(r, options));
    }

    // Prefer the bundled wwwroot (the HttpArena static set, copied next to the binary);
    // fall back to a tiny generated sample dir so `file` mode always has something to serve.
    private static string DefaultAssetDir()
    {
        string www = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        return Directory.Exists(www) ? www : EnsureSampleDir();
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
