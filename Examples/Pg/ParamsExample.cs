using ioxide;
using ioxide.pg;

namespace Examples.Pg;

/// <summary>
/// Parameterized (prepared) queries. Each distinct SQL text is auto-Parsed once per connection, then
/// Bind/Execute'd on every reuse, so the server plans it once. <c>/add/N</c> binds an int parameter,
/// <c>/upper/TEXT</c> binds a text parameter.
/// </summary>
public static class ParamsExample
{
    public static async Task Handle(Reactor r, Connection conn)
    {
        PgPool pool = r.GetService<PgPool>();

        try
        {
            while (true)
            {
                RecvSnapshot snapshot = await conn.ReadAsync();
                string path = Http.ReadPath(conn, snapshot);

                if (path.StartsWith("/upper/", StringComparison.Ordinal))
                {
                    string text = path["/upper/".Length..];
                    PgResult result = await pool.QueryAsync("SELECT upper($1)", [PgParam.Text(text)]);

                    Http.WriteText(conn, 200, "OK", $"upper={result.Value}");
                }
                else
                {
                    long n = Http.TrailingInt(path, 41);
                    PgResult result = await pool.QueryAsync("SELECT $1::bigint + 1", [PgParam.Int(n)]);

                    Http.WriteText(conn, 200, "OK", $"{n}+1={result.Value}");
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
