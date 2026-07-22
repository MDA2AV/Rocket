using ioxide.pg;

namespace Ioxide.E2E;

/// <summary>Postgres driver over the ring: queries, parameters, streaming, errors, timeouts.</summary>
internal static class PgTests
{
    public static void Register(Runner runner, (string Host, int Port) pg, bool pgUp)
    {
        // ---- pg pool fails fast on a dead backend (#1) - needs NO live pg ----
        runner.Test("pg: dead backend fails fast, no hang (#1)", () =>
        {
            PgOptions dead = PgOpts(pg) with { Port = 5599 };
            int port = TestServer.Start(Handlers.Pg, r => PgPool.Start(r, dead));
            (int status, _) = Client.Get(port, "/", timeoutMs: 8000);
            Assert.Equal(500, status);   // PgException surfaced quickly, not a hang
        });

        // ---- pg (needs the sidecar) ----
        runner.Test("pg: SELECT 42", () =>
        {
            int port = TestServer.Start(Handlers.Pg, r => PgPool.Start(r, PgOpts(pg)));
            (int status, string body) = Client.Get(port, "/");
            Assert.Equal(200, status);
            Assert.Equal("42", body);
        }, skip: !pgUp);

        runner.Test("pg: prepared int parameter", () =>
        {
            int port = TestServer.Start(Handlers.Pg, r => PgPool.Start(r, PgOpts(pg)));
            (int status, string body) = Client.Get(port, "/add/41");
            Assert.Equal(200, status);
            Assert.Equal("42", body);
        }, skip: !pgUp);

        runner.Test("pg: row streaming", () =>
        {
            int port = TestServer.Start(Handlers.Pg, r => PgPool.Start(r, PgOpts(pg)));
            (int status, string body) = Client.Get(port, "/rows");
            Assert.Equal(200, status);
            Assert.Equal("rows=5", body);
        }, skip: !pgUp);

        runner.Test("pg: server error then connection stays usable", () =>
        {
            int port = TestServer.Start(Handlers.Pg, r => PgPool.Start(r, PgOpts(pg)));
            (int badStatus, string sqlState) = Client.Get(port, "/bad");
            Assert.Equal(500, badStatus);
            Assert.Equal("42P01", sqlState);   // undefined_table

            (int okStatus, string body) = Client.Get(port, "/");
            Assert.Equal(200, okStatus);
            Assert.Equal("42", body);
        }, skip: !pgUp);

        runner.Test("pg: command timeout (#2)", () =>
        {
            int port = TestServer.Start(Handlers.PgSlow, r => PgPool.Start(r, PgOpts(pg, commandTimeoutMs: 1000)));
            (int status, _) = Client.Get(port, "/slow", timeoutMs: 8000);
            Assert.Equal(503, status);
        }, skip: !pgUp);
    }

    private static PgOptions PgOpts((string Host, int Port) pg, int commandTimeoutMs = 30_000) => new()
    {
        Host = pg.Host,
        Port = (ushort)pg.Port,
        User = Environment.GetEnvironmentVariable("EXAMPLES_PG_USER") ?? "bench",
        Database = Environment.GetEnvironmentVariable("EXAMPLES_PG_DB") ?? "bench",
        Password = Environment.GetEnvironmentVariable("EXAMPLES_PG_PASSWORD"),
        PoolSize = 2,
        CommandTimeoutMs = commandTimeoutMs,
    };
}
