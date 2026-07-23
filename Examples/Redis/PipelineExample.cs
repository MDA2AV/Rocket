using ioxide;
using ioxide.redis;

namespace Examples.Redis;

/// <summary>
/// Explicit pipelining: several commands sent back to back on one connection in a single round trip,
/// with replies returned in order. Here SET, INCR, then GET on the same key.
/// </summary>
public static class PipelineExample
{
    public static async Task Handle(Reactor r, TcpConnection conn)
    {
        RedisPool pool = r.GetService<RedisPool>();

        try
        {
            while (true)
            {
                RecvSnapshot snapshot = await conn.ReadAsync();
                Http.ReadPath(conn, snapshot);   // drain the request; this demo ignores the path

                RespValue[] replies = await pool.PipelineAsync(
                    new RedisCommand("SET", "ex:pipe", "1"),
                    new RedisCommand("INCR", "ex:pipe"),
                    new RedisCommand("GET", "ex:pipe"));

                Http.WriteText(conn, 200, "OK",
                    $"set={replies[0].AsString()} incr={replies[1].AsInteger()} get={replies[2].AsString()}");

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
