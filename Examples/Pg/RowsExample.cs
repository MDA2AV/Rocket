using System.Text;
using ioxide;
using ioxide.pg;

namespace Examples.Pg;

/// <summary>
/// Streaming multiple rows. <c>/rows/N</c> runs <c>generate_series(1, N)</c> and streams each row to
/// the inline callback, read by column name (<see cref="PgRow.Field(string)"/>) - the RowDescription
/// is captured because this is a row-streaming query, so column names and type OIDs are available.
/// </summary>
public static class RowsExample
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

                long count = Math.Clamp(Http.TrailingInt(path, 5), 1, 100);

                var values = new StringBuilder();
                string columns = "";

                int rows = await pool.QueryRowsAsync(
                    $"SELECT n, n * n AS square FROM generate_series(1, {count}) AS n",
                    row =>
                    {
                        if (columns.Length == 0)
                        {
                            foreach (PgColumn column in row.Columns)
                            {
                                columns += (columns.Length == 0 ? "" : ", ") + $"{column.Name}:oid{column.TypeOid}";
                            }
                        }

                        string n = Encoding.ASCII.GetString(row.Field("n"));
                        string square = Encoding.ASCII.GetString(row.Field("square"));
                        values.Append(n).Append("^2=").Append(square).Append(' ');
                    });

                Http.WriteText(conn, 200, "OK", $"cols=[{columns}] rows={rows}: {values}");
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
