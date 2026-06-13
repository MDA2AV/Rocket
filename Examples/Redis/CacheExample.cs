using ioxide;
using ioxide.redis;

namespace Examples.Redis;

/// <summary>
/// Cache-aside: GET the key; on a miss, compute a value and SET it with a TTL, then return it. Shows
/// GET and SET ... EX through the pool. The request path is the cache key.
/// </summary>
public static class CacheExample
{
    public static async Task Handle(Reactor r, Connection conn)
    {
        RedisPool pool = r.GetService<RedisPool>();

        try
        {
            while (true)
            {
                RecvSnapshot snapshot = await conn.ReadAsync();
                string key = "ex:cache:" + Http.ReadPath(conn, snapshot).TrimStart('/');

                string? cached = await pool.GetAsync(key);
                if (cached != null)
                {
                    Http.WriteText(conn, 200, "OK", $"hit {key} = {cached}");
                }
                else
                {
                    string value = $"value-{key.Length}";
                    await pool.SetExAsync(key, value, seconds: 60);
                    Http.WriteText(conn, 200, "OK", $"miss {key} = {value} (cached 60s)");
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
