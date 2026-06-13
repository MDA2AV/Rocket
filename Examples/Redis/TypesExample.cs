using ioxide;
using ioxide.redis;

namespace Examples.Redis;

/// <summary>
/// RESP reply types through the generic ExecuteAsync: <c>/incr</c> returns a RESP integer, <c>/hash</c>
/// and <c>/list</c> return RESP arrays of bulk strings. Shows how RespValue exposes each one
/// (<see cref="RespValue.AsInteger"/>, <see cref="RespValue.Items"/>, <see cref="RespValue.AsString"/>).
/// </summary>
public static class TypesExample
{
    public static async Task Handle(Reactor r, Connection conn)
    {
        RedisPool pool = r.GetService<RedisPool>();

        try
        {
            while (true)
            {
                RecvSnapshot snapshot = await conn.ReadAsync();
                string path = Http.ReadPath(conn, snapshot);

                string body;
                switch (path)
                {
                    case "/incr":
                        body = await Counter(pool);
                        break;
                    case "/hash":
                        body = await Hash(pool);
                        break;
                    case "/list":
                        body = await List(pool);
                        break;
                    default:
                        body = "try /incr, /hash, /list";
                        break;
                }

                Http.WriteText(conn, 200, "OK", body);
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

    // RESP integer reply.
    private static async Task<string> Counter(RedisPool pool)
    {
        long n = (await pool.ExecuteAsync("INCR", "ex:counter")).AsInteger();
        return $"counter = {n}";
    }

    // RESP array of bulk strings: field, value, field, value, ...
    private static async Task<string> Hash(RedisPool pool)
    {
        await pool.ExecuteAsync("HSET", "ex:hash", "lang", "csharp", "rps", "1000000");
        RespValue all = await pool.ExecuteAsync("HGETALL", "ex:hash");
        return "hash = [" + string.Join(", ", all.Items.Select(v => v.AsString())) + "]";
    }

    // RESP array of bulk strings.
    private static async Task<string> List(RedisPool pool)
    {
        await pool.ExecuteAsync("DEL", "ex:list");
        await pool.ExecuteAsync("RPUSH", "ex:list", "a", "b", "c");
        RespValue items = await pool.ExecuteAsync("LRANGE", "ex:list", "0", "-1");
        return "list = [" + string.Join(", ", items.Items.Select(v => v.AsString())) + "]";
    }
}
