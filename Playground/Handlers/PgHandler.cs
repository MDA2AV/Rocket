using ioxide;
using ioxide.pg;
using Playground.Http;

namespace Playground.Handlers;

/// <summary>
/// pg - each request runs a query through the reactor's pool; a server error becomes a 500.
/// Paths: / → SELECT 42 · /sleep → 100ms query (pool concurrency demo) · /hang → 10s · /err → error.
/// </summary>
internal static class PgHandler
{
    public static Task Handle(Reactor reactor, TcpConnection conn)
        => ConnectionLoop.ServeAsync(conn, new PgResponder(reactor.GetService<PgPool>()));

    private readonly struct PgResponder(PgPool pool) : ITcpResponder
    {
        public async ValueTask RespondAsync(TcpConnection conn, RecvSnapshot snapshot)
        {
            string path = RequestParser.ReadPath(conn, snapshot);

            string sql = path switch
            {
                "/sleep" => "SELECT 42 FROM pg_sleep(0.1)",
                "/hang"  => "SELECT pg_sleep(10)",
                "/err"   => "SELECT * FROM this_table_does_not_exist",
                _        => "SELECT 42",
            };

            try
            {
                PgResult result = await pool.QueryAsync(sql);
                Responses.WriteDbResult(conn, result.Value ?? "");
            }
            catch (PgException e)
            {
                Console.Error.WriteLine($"[pg] query failed: {e.Message}");
                conn.Write(Responses.ServerError);
            }

            await conn.FlushAsync();
        }
    }
}
