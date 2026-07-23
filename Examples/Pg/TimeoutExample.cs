using ioxide;
using ioxide.pg;

namespace Examples.Pg;

/// <summary>
/// Per-command timeout. This mode runs with a short <c>PgOptions.CommandTimeoutMs</c> (see Program).
/// <c>/slow</c> runs a query longer than the timeout: the pool's reactor-thread sweep tears the
/// connection down and the awaiting query throws <see cref="PgException"/>, so a slow or silent
/// backend can't park a request forever. The pool replaces the torn-down connection in the background.
/// </summary>
public static class TimeoutExample
{
    public static async Task Handle(Reactor r, TcpConnection conn)
    {
        PgPool pool = r.GetService<PgPool>();

        try
        {
            while (true)
            {
                RecvSnapshot snapshot = await conn.ReadAsync();
                string path = Http.ReadPath(conn, snapshot);

                string sql = path == "/slow"
                    ? "SELECT pg_sleep(5)"
                    : "SELECT 42";

                try
                {
                    PgResult result = await pool.QueryAsync(sql);
                    Http.WriteText(conn, 200, "OK", $"value={result.Value}");
                }
                catch (PgException e)
                {
                    Http.WriteText(conn, 503, "Service Unavailable", $"timed out: {e.Message}");
                }

                await conn.FlushAsync();

                if (snapshot.IsClosed)
                {
                    return;
                }

                conn.ResetRead();
            }
        }
        finally
        {
            conn.DecRef();
        }
    }
}
