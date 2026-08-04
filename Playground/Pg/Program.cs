using System.Text;
using ioxide;
using ioxide.pg;
using ioxide.utils;
using Playground.Shared;

// ─────────────────────────────────────────────────────────────────────────────────────────────
//  pg - a Postgres-backed ioxide server, whole. A pool per reactor; connect, query and row
//  streaming are all ring operations, so a request never leaves the thread it arrived on.
//
//      docker run --rm -d -p 5432:5432 -e POSTGRES_USER=bench -e POSTGRES_DB=bench \
//        -e POSTGRES_HOST_AUTH_METHOD=trust postgres:18
//      dotnet run -c Release --project Playground/Pg
//      curl http://127.0.0.1:8080/          # -> db=42
//
//      /        SELECT 42
//      /sleep   a 100ms query, to watch pool concurrency
//      /hang    a 10s query, to watch the command timeout
//      /err     a server error -> 500, and the connection stays usable
//
//  Needs: ioxide, ioxide.pg
// ─────────────────────────────────────────────────────────────────────────────────────────────

var config = new ServerConfig
{
    ReactorCount = Env.Int("PLAYGROUND_REACTORS", Environment.ProcessorCount),
    Tcp = new TcpOptions
    {
        Port = Env.Port("PLAYGROUND_PORT", 8080),
    },
};

var pgOptions = new PgOptions
{
    Host = Env.Str("PLAYGROUND_PG_HOST", "127.0.0.1"),
    Port = Env.Port("PLAYGROUND_PG_PORT", 5432),
    User = Env.Str("PLAYGROUND_PG_USER", "bench"),
    Database = Env.Str("PLAYGROUND_PG_DB", "bench"),
    PoolSize = Env.Int("PLAYGROUND_PG_POOL", 4),          // per reactor, not global
    CommandTimeoutMs = Env.Int("PLAYGROUND_PG_TIMEOUT", 30_000),
};

var threads = new Thread[config.ReactorCount];

for (int i = 0; i < threads.Length; i++)
{
    var reactor = new Reactor(i, config);

    // OnStart runs ON the reactor thread, so every connection the pool opens belongs to THIS
    // reactor's ring. Start registers the pool as a reactor service; the handler fetches it back
    // with GetService. One pool per reactor, no sharing, no lock.
    reactor.OnStart = r => PgPool.Start(r, pgOptions);

    reactor.TcpHandle = async (r, conn) =>
    {
        PgPool pg = r.GetService<PgPool>();

        try
        {
            while (true)
            {
                RecvSnapshot snapshot = await conn.ReadAsync();

                // Drain the recv, pulling the request target out on the way past. The buffers must
                // go back to the ring, so read what you need before returning them.
                string path = "/";
                while (conn.TryGetItem(snapshot, out SpscRecvRing.Item item))
                {
                    if (item.HasBuffer)
                    {
                        if (TryReadTarget(item.AsSpan(), out ReadOnlySpan<byte> target))
                        {
                            path = Encoding.ASCII.GetString(target);
                        }
                        conn.ReturnBuffer(in item);
                    }
                }

                string sql = path switch
                {
                    "/sleep" => "SELECT 42 FROM pg_sleep(0.1)",
                    "/hang"  => "SELECT pg_sleep(10)",
                    "/err"   => "SELECT * FROM this_table_does_not_exist",
                    _        => "SELECT 42",
                };

                try
                {
                    // io_uring send + recv to Postgres, on the same ring that accepted the request.
                    // The continuation resumes inline, on this thread.
                    PgResult result = await pg.QueryAsync(sql);

                    string responseBody = $"db={result.Value}";
                    conn.Write(Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {responseBody.Length}\r\n\r\n{responseBody}"));
                }
                catch (PgException e)
                {
                    // A server error is just an exception. The connection goes back to the pool
                    // usable - Postgres told us about a bad query, it didn't break the socket.
                    Console.Error.WriteLine($"[pg] query failed: {e.Message}");
                    conn.Write("HTTP/1.1 500 Internal Server Error\r\nContent-Length: 0\r\n\r\n"u8);
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

Console.WriteLine($"[pg] {config.ReactorCount} reactors on :{config.Tcp.Port} "
                + $"-> {pgOptions.Host}:{pgOptions.Port}, {pgOptions.PoolSize} connections each");

foreach (Thread thread in threads)
{
    thread.Join();
}

// "GET /sleep?x=1 HTTP/1.1" -> "/sleep". Your framework of choice would do this for you; ioxide
// deliberately doesn't, so here it is in full.
static bool TryReadTarget(ReadOnlySpan<byte> request, out ReadOnlySpan<byte> target)
{
    target = default;

    int firstSpace = request.IndexOf((byte)' ');
    if (firstSpace < 0) return false;

    ReadOnlySpan<byte> afterMethod = request[(firstSpace + 1)..];
    int secondSpace = afterMethod.IndexOf((byte)' ');
    if (secondSpace < 0) return false;

    target = afterMethod[..secondSpace];

    int query = target.IndexOf((byte)'?');
    if (query >= 0) target = target[..query];

    return true;
}
