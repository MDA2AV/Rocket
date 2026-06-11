using System.Security.Cryptography.X509Certificates;
using ioxide;
using ioxide.pg;
using ioxide.tls;
using Examples.Tls;

namespace Examples;

/// <summary>
/// Runs the landing-page examples, grouped by category:
///
///   pg  - every request runs a query through a per-reactor PgPool
///         (needs a trust/SCRAM Postgres, user/db "bench"; override via EXAMPLES_PG_*)
///   raw - no database: read, return buffers, answer fixed plaintext
///   tls - a medium response over TLS, two ways: kTLS (ioxide.tls) vs SslStream
///         (self-signed cert generated at startup; body size via EXAMPLES_TLS_BODY, default 8 KB)
///
/// Pick the mode by arg or by the EXAMPLES_MODE env var (the arg wins; default pg-pipes):
///   dotnet run --project Examples -- raw-shared
///   EXAMPLES_MODE=raw-shared ./Examples/bin/Release/net10.0/Examples
///
/// Modes: pg-pipes | pg-shared | pg-incremental | raw-pipes | raw-shared | raw-incremental
///        | tls-ktls | tls-sslstream
///
/// Benchmark numbers live in Examples/RESULTS.md.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        // Arg wins; otherwise EXAMPLES_MODE (so a bare ./Examples works); else default.
        string mode = args.Length > 0
            ? args[0]
            : Environment.GetEnvironmentVariable("EXAMPLES_MODE") ?? "pg-pipes";

        Console.WriteLine(mode);

        (ServerConfig config, Func<Reactor, Connection, Task> handle, bool needsPg, bool needsTls) = mode switch
        {
            "pg-pipes"        => (Configs.Shared,      Pg.PipesExample.Handle,        true,  false),
            "pg-shared"       => (Configs.Shared,      Pg.SharedExample.Handle,       true,  false),
            "pg-incremental"  => (Configs.Incremental, (Func<Reactor, Connection, Task>)Pg.IncrementalExample.Handle, true, false),
            "raw-pipes"       => (Configs.Shared,      Raw.PipesExample.Handle,       false, false),
            "raw-shared"      => (Configs.Shared,      Raw.SharedExample.Handle,      false, false),
            "raw-incremental" => (Configs.Incremental, Raw.IncrementalExample.Handle, false, false),
            "tls-ktls"        => (Configs.Shared,      KtlsExample.Handle,            false, true),
            "tls-sslstream"   => (Configs.Shared,      SslStreamExample.Handle,       false, true),
            _ => throw new ArgumentException($"unknown mode '{mode}'"),
        };

        var pgOptions = new PgOptions
        {
            Host = Environment.GetEnvironmentVariable("EXAMPLES_PG_HOST") ?? "127.0.0.1",
            Port = ushort.TryParse(Environment.GetEnvironmentVariable("EXAMPLES_PG_PORT"), out ushort p) ? p : (ushort)5432,
            User = Environment.GetEnvironmentVariable("EXAMPLES_PG_USER") ?? "bench",
            Database = Environment.GetEnvironmentVariable("EXAMPLES_PG_DB") ?? "bench",
            Password = Environment.GetEnvironmentVariable("EXAMPLES_PG_PASSWORD"),
            PoolSize = int.TryParse(Environment.GetEnvironmentVariable("EXAMPLES_PG_POOL"), out int ps) ? ps : 4,
        };

        X509Certificate2? cert = null;
        TlsOptions? tlsOptions = null;
        if (needsTls)
        {
            cert = TlsCert.EnsureCert();
            tlsOptions = new TlsOptions { CertificatePath = TlsCert.CertPath, KeyPath = TlsCert.KeyPath };
            SslStreamExample.Init(cert);
            Console.WriteLine($"[examples] {mode}: TLS on :{config.Port}, {Body.Size}-byte body, cert {TlsCert.CertPath}");
        }
        else
        {
            Console.WriteLine($"[examples] {mode}: {config.ReactorCount} reactors on :{config.Port}");
        }

        var threads = new Thread[config.ReactorCount];

        for (int i = 0; i < config.ReactorCount; i++)
        {
            var reactor = new Reactor(i, config);
            if (needsPg)
            {
                reactor.OnStart = r => PgPool.Start(r, pgOptions);
            }
            else if (needsTls && mode == "tls-ktls")
            {
                var options = tlsOptions!;
                reactor.OnStart = r => TlsService.Start(r, options);
            }
            reactor.Handle = handle;

            threads[i] = new Thread(reactor.Run) { Name = $"reactor-{i}", IsBackground = false };
            threads[i].Start();
        }

        foreach (var t in threads) t.Join();
        return 0;
    }
}
