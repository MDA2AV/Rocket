using System.Buffers;
using System.Text;
using ioxide;
using ioxide.pg;

namespace Examples.Pg;

/// <summary>
/// The landing page's "pipes" tab, runnable: ConnectionPipeReader owns the carry (partial
/// requests wait inside it, zero-copy), ConnectionPipeWriter stages into the write slab,
/// and every request runs a query through the per-reactor PgPool.
/// </summary>
public static class PipesExample
{
    public static async Task Handle(Reactor r, TcpConnection conn)
    {
        var pool = r.GetService<PgPool>();

        var reader = new ConnectionPipeReader(conn);
        var writer = new ConnectionPipeWriter(conn);

        while (true)
        {
            // io_uring recv - resumes inline on the reactor.
            var result = await reader.ReadAsync();
            var buffer = result.Buffer;   // every unconsumed byte received so far

            // Walk every complete request; a partial one stays buffered.
            bool respond = false;
            while (Http.TryParseRequest(ref buffer, out Request request))
            {
                // io_uring send + recv to Postgres, on the same ring.
                var rows = await pool.QueryAsync(Http.SqlFor(request.Path));

                string body = $"db={rows.Value}";
                writer.Write(Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {body.Length}\r\n\r\n{body}"));
                respond = true;
            }

            // Consumed bytes release their buffers; the partial tail is kept.
            reader.AdvanceTo(buffer.Start, buffer.End);

            if (respond) await writer.FlushAsync();   // once per batch

            if (result.IsCompleted)
            {
                reader.Complete();
                writer.Complete();
                conn.DecRef();
                return;
            }
        }
    }
}
