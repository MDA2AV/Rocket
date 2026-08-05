using System.Text;
using ioxide;
using ioxide.redis;
using ioxide.utils;
using Playground.Shared;

// ─────────────────────────────────────────────────────────────────────────────────────────────
//  redis - a RedisPool per reactor, driven on the same ring that accepted the request. Every
//  await below resumes inline on the reactor thread; commands from concurrent requests share
//  the pool's connections and pipeline automatically.
//
//      docker run --rm -p 6379:6379 redis            # or point PLAYGROUND_REDIS_* elsewhere
//      dotnet run -c Release --project Playground/Redis
//
//      curl http://127.0.0.1:8080/                   # GET hot path (the wrk-able route)
//      curl http://127.0.0.1:8080/cache/user42       # cache-aside: GET, miss -> SET EX
//      curl http://127.0.0.1:8080/incr               # RESP integer reply
//      curl http://127.0.0.1:8080/hash               # RESP array (HGETALL)
//      curl http://127.0.0.1:8080/list               # RESP array (LRANGE)
//      curl http://127.0.0.1:8080/pipeline           # SET+INCR+GET in one round trip
//
//  Needs: ioxide.redis
// ─────────────────────────────────────────────────────────────────────────────────────────────

var config = new ServerConfig
{
    ReactorCount = Env.Int("PLAYGROUND_REACTORS", Environment.ProcessorCount),
    Tcp = new TcpOptions
    {
        Port = Env.Port("PLAYGROUND_PORT", 8080),
    },
};

var redisOptions = new RedisOptions
{
    Host     = Env.Str("PLAYGROUND_REDIS_HOST", "127.0.0.1"),
    Port     = Env.Port("PLAYGROUND_REDIS_PORT", 6379),
    Password = Env.StrOrNull("PLAYGROUND_REDIS_PASSWORD"),
    PoolSize = Env.Int("PLAYGROUND_REDIS_POOL", 4),
};

var threads = new Thread[config.ReactorCount];

for (int i = 0; i < threads.Length; i++)
{
    var reactor = new Reactor(i, config);

    // The pool lives on the reactor: its connections ride this reactor's ring, so a query is
    // an io_uring send + recv with the continuation resuming right here.
    reactor.OnStart = r =>
    {
        RedisPool pool = RedisPool.Start(r, redisOptions);
        _ = SeedAsync(pool);   // the "/" route reads this key; seed it so the demo answers
    };

    reactor.TcpHandle = async (r, conn) =>
    {
        RedisPool pool = r.GetService<RedisPool>();

        try
        {
            while (true)
            {
                RecvSnapshot snapshot = await conn.ReadAsync();
                string path = ReadPath(conn, snapshot);

                try
                {
                    string body = path switch
                    {
                        // Cache-aside: try the key; on a miss compute, SET with a TTL, return.
                        var p when p.StartsWith("/cache/", StringComparison.Ordinal)
                            => await CacheAside(pool, p["/cache/".Length..]),

                        // One RESP reply type per route, through the generic ExecuteAsync.
                        "/incr"     => $"counter = {(await pool.ExecuteAsync("INCR", "play:counter")).AsInteger()}",
                        "/hash"     => await Hash(pool),
                        "/list"     => await List(pool),

                        // Several commands, one round trip, replies in order.
                        "/pipeline" => await Pipeline(pool),

                        // The hot path: a single GET per request. wrk this one.
                        _           => $"redis = {await pool.GetAsync("play:bench") ?? "(nil)"}",
                    };

                    WriteText(conn, "200 OK", body);
                }
                catch (RedisException e)
                {
                    // A server error is an exception, not a broken socket: the connection has
                    // resynced and goes back to the pool usable.
                    WriteText(conn, "500 Internal Server Error", e.Message);
                }

                await conn.FlushAsync();

                if (snapshot.IsClosed) return;
                conn.ResetRead();
            }
        }
        finally
        {
            conn.DecRef();
        }
    };

    threads[i] = new Thread(reactor.Run) { Name = $"reactor-{i}" };
    threads[i].Start();
}

Console.WriteLine($"[redis] {config.ReactorCount} reactors on :{config.Tcp!.Port} "
                + $"-> {redisOptions.Host}:{redisOptions.Port}, {redisOptions.PoolSize} connections each");

foreach (Thread thread in threads)
{
    thread.Join();
}

static async Task SeedAsync(RedisPool pool)
{
    try { await pool.ExecuteAsync("SET", "play:bench", "42"); }
    catch (Exception e) { Console.Error.WriteLine($"[redis] seed failed: {e.Message}"); }
}

static async Task<string> CacheAside(RedisPool pool, string key)
{
    key = "play:cache:" + key;
    string? cached = await pool.GetAsync(key);
    if (cached != null)
    {
        return $"hit {key} = {cached}";
    }

    string value = $"value-{key.Length}";           // stands in for the expensive computation
    await pool.SetExAsync(key, value, seconds: 60);
    return $"miss {key} = {value} (cached 60s)";
}

// RESP array of bulk strings: field, value, field, value, ...
static async Task<string> Hash(RedisPool pool)
{
    await pool.ExecuteAsync("HSET", "play:hash", "lang", "csharp", "engine", "io_uring");
    RespValue all = await pool.ExecuteAsync("HGETALL", "play:hash");
    return "hash = [" + string.Join(", ", all.Items.Select(v => v.AsString())) + "]";
}

static async Task<string> List(RedisPool pool)
{
    await pool.ExecuteAsync("DEL", "play:list");
    await pool.ExecuteAsync("RPUSH", "play:list", "a", "b", "c");
    RespValue items = await pool.ExecuteAsync("LRANGE", "play:list", "0", "-1");
    return "list = [" + string.Join(", ", items.Items.Select(v => v.AsString())) + "]";
}

static async Task<string> Pipeline(RedisPool pool)
{
    RespValue[] replies = await pool.PipelineAsync(
        new RedisCommand("SET", "play:pipe", "1"),
        new RedisCommand("INCR", "play:pipe"),
        new RedisCommand("GET", "play:pipe"));
    return $"set={replies[0].AsString()} incr={replies[1].AsInteger()} get={replies[2].AsString()}";
}

// "GET /cache/x HTTP/1.1" -> "/cache/x", draining the recv on the way past.
static string ReadPath(TcpConnection conn, RecvSnapshot snapshot)
{
    string path = "/";
    while (conn.TryGetItem(snapshot, out SpscRecvRing.Item item))
    {
        if (item.HasBuffer)
        {
            ReadOnlySpan<byte> request = item.AsSpan();
            int firstSpace = request.IndexOf((byte)' ');
            if (firstSpace >= 0)
            {
                ReadOnlySpan<byte> rest = request[(firstSpace + 1)..];
                int secondSpace = rest.IndexOf((byte)' ');
                if (secondSpace > 0)
                {
                    ReadOnlySpan<byte> target = rest[..secondSpace];
                    int query = target.IndexOf((byte)'?');
                    path = Encoding.ASCII.GetString(query >= 0 ? target[..query] : target);
                }
            }
            conn.ReturnBuffer(in item);
        }
    }
    return path;
}

static void WriteText(TcpConnection conn, string status, string body)
    => conn.Write(Encoding.ASCII.GetBytes(
        $"HTTP/1.1 {status}\r\nContent-Type: text/plain\r\nContent-Length: {body.Length}\r\n\r\n{body}"));
