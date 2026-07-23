using ioxide;
using ioxide.pg;

namespace Examples.Pg;

/// <summary>
/// Server errors surface as <see cref="PgException"/> (severity + SQLSTATE) and leave the connection
/// usable: the protocol resyncs at ReadyForQuery, so the next query on the same connection still
/// works. <c>/bad</c> runs a failing query; anything else runs <c>SELECT 42</c>.
/// </summary>
public static class ErrorExample
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

                string sql = path == "/bad"
                    ? "SELECT * FROM no_such_table"
                    : "SELECT 42";

                try
                {
                    PgResult result = await pool.QueryAsync(sql);
                    Http.WriteText(conn, 200, "OK", $"value={result.Value}");
                }
                catch (PgException e)
                {
                    Http.WriteText(conn, 500, "Internal Server Error", $"pg {e.SqlState}: {e.Message}");
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
