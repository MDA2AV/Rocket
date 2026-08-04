using System.Buffers.Text;
using System.Text;
using ioxide;
using ioxide.pg;
using Playground.Shared;
using Playground.Shared.Http;

// pg - a PgPool per reactor; each request runs a query on the reactor's own ring and resumes inline.
//
//   /       SELECT 42
//   /sleep  a 100ms query, to watch pool concurrency
//   /hang   a 10s query, to watch the command timeout
//   /err    a server error, which becomes a 500 while the connection stays usable

var options = new PgOptions
{
    Host = Env.Str("PLAYGROUND_PG_HOST", "127.0.0.1"),
    Port = Env.Port("PLAYGROUND_PG_PORT", 5432),
    User = Env.Str("PLAYGROUND_PG_USER", "bench"),
    Database = Env.Str("PLAYGROUND_PG_DB", "bench"),
    PoolSize = Env.Int("PLAYGROUND_PG_POOL", 4),
    CommandTimeoutMs = Env.Int("PLAYGROUND_PG_TIMEOUT", 30_000),
};

return PlaygroundHost.Run(new PlaygroundSample
{
    Name = "pg",
    Summary = $"a PgPool per reactor against {options.Host}:{options.Port}",
    // OnStart runs on the reactor thread, so the pool's connections belong to that reactor's ring.
    Start = reactor => PgPool.Start(reactor, options),
    Tcp = (reactor, conn) => ConnectionLoop.ServeAsync(conn, new PgResponder(reactor.GetService<PgPool>())),
});

internal readonly struct PgResponder(PgPool pool) : ITcpResponder
{
    public async ValueTask RespondAsync(TcpConnection conn, RecvSnapshot snapshot)
    {
        string path = RequestParser.ReadPath(conn, snapshot);

        string sql = path switch
        {
            "/sleep" => "SELECT 42 FROM pg_sleep(0.1)",
            "/hang"  => "SELECT pg_sleep(10)",
            "/err"   => "SELECT * FROM this_table_does_not_exist",
            _        => "SELECT 42",
        };

        try
        {
            PgResult result = await pool.QueryAsync(sql);
            WriteDbResult(conn, result.Value ?? "");
        }
        catch (PgException e)
        {
            Console.Error.WriteLine($"[pg] query failed: {e.Message}");
            conn.Write(Responses.ServerError);
        }

        await conn.FlushAsync();
    }

    /// <summary>Frame a "db=&lt;value&gt;" plaintext response into the write slab - no allocation.</summary>
    private static void WriteDbResult(TcpConnection conn, string value)
    {
        Span<byte> response = stackalloc byte[160];
        int position = 0;

        position += Responses.Copy(response[position..],
            "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: "u8);

        int bodyLength = 3 + value.Length;   // "db=" + value
        Utf8Formatter.TryFormat(bodyLength, response[position..], out int digits);
        position += digits;

        position += Responses.Copy(response[position..], "\r\n\r\ndb="u8);
        position += Encoding.ASCII.GetBytes(value, response[position..]);

        conn.Write(response[..position]);
    }
}
