namespace Ioxide.E2E;

/// <summary>
/// End-to-end suite: starts real ioxide servers and drives them over real sockets, asserting
/// behavior (not timings or throughput, so it isn't brittle). pg / redis / kTLS tests skip when the
/// dependency is unreachable. Exit code is non-zero if any test fails. One file per area:
/// CoreTests, HardeningTests, UdpTests, QuicTests, PgTests, RedisTests, FileTests, TlsTests.
/// </summary>
internal static class Program
{
    private static int Main()
    {
        var runner = new Runner();

        (string Host, int Port) pg = (Env("EXAMPLES_PG_HOST", "127.0.0.1"), EnvInt("EXAMPLES_PG_PORT", 5432));
        (string Host, int Port) redis = (Env("EXAMPLES_REDIS_HOST", "127.0.0.1"), EnvInt("EXAMPLES_REDIS_PORT", 6379));

        bool pgUp = Sidecars.Reachable(pg.Host, pg.Port);
        bool redisUp = Sidecars.Reachable(redis.Host, redis.Port);
        bool ktls = Sidecars.KtlsAvailable();

        Console.WriteLine(
            $"sidecars: pg {(pgUp ? "up" : "down")} ({pg.Host}:{pg.Port}), " +
            $"redis {(redisUp ? "up" : "down")} ({redis.Host}:{redis.Port}), " +
            $"kTLS {(ktls ? "available" : "absent")}\n");

        CoreTests.Register(runner);
        HardeningTests.Register(runner);
        UdpTests.Register(runner);
        QuicTests.Register(runner);
        QuicEngineTests.Register(runner);
        H3Tests.Register(runner);
        Http3Tests.Register(runner);
        HttpClientTests.Register(runner);
        Http3ClientTests.Register(runner);
        Http2ClientTests.Register(runner);
        RingHttpClientTests.Register(runner);
        PgTests.Register(runner, pg, pgUp);
        RedisTests.Register(runner, redis, redisUp);
        FileTests.Register(runner);
        TlsTests.Register(runner, ktls);

        return runner.Summary();
    }

    private static string Env(string key, string fallback) => Environment.GetEnvironmentVariable(key) ?? fallback;
    private static int EnvInt(string key, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(key), out int v) ? v : fallback;
}
