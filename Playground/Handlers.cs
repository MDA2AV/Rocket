using System.Buffers.Text;
using System.IO.Pipelines;
using System.Text;
using ioxide;
using ioxide.file;
using ioxide.pg;
using ioxide.utils;

namespace Playground;

/// <summary>
/// The three request handlers the Playground can serve. Each is the same connection loop - read,
/// respond, flush, repeat until the client closes - differing only in how it produces the response.
/// </summary>
internal static class Handlers
{
    private static ReadOnlySpan<byte> Ok =>
        "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 2\r\n\r\nok"u8;

    private static ReadOnlySpan<byte> NotFound =>
        "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\n\r\n"u8;

    private static ReadOnlySpan<byte> ServerError =>
        "HTTP/1.1 500 Internal Server Error\r\nContent-Length: 0\r\n\r\n"u8;

    /// <summary>raw - a fixed plaintext response; no I/O beyond the socket.</summary>
    public static async Task Raw(Reactor reactor, Connection conn)
    {
        try
        {
            while (true)
            {
                RecvSnapshot snapshot = await conn.ReadAsync();
                Drain(conn, snapshot);

                conn.Write(Ok);
                await conn.FlushAsync();

                if (snapshot.IsClosed) return;
                conn.ResetRead();
            }
        }
        finally
        {
            conn.DecRef();
        }
    }

    /// <summary>
    /// pipe - identical workload to raw, but read and written through the PipeReader/PipeWriter
    /// adapters. Exists to benchmark the adapter overhead against the raw API.
    /// </summary>
    public static async Task Pipe(Reactor reactor, Connection conn)
    {
        var reader = new ConnectionPipeReader(conn);
        var writer = new ConnectionPipeWriter(conn);

        try
        {
            while (true)
            {
                ReadResult result = await reader.ReadAsync();

                // Raw mode doesn't parse the request either - consume everything.
                reader.AdvanceTo(result.Buffer.End);

                Ok.CopyTo(writer.GetSpan(Ok.Length));
                writer.Advance(Ok.Length);
                await writer.FlushAsync();

                if (result.IsCompleted) return;
            }
        }
        finally
        {
            reader.Complete();
            conn.DecRef();
        }
    }

    /// <summary>
    /// hop - raw, but every request bounces through the thread pool (Task.Yield) first.
    /// Exercises the off-reactor queues and the eventfd wake.
    /// </summary>
    public static async Task Hop(Reactor reactor, Connection conn)
    {
        try
        {
            while (true)
            {
                RecvSnapshot snapshot = await conn.ReadAsync();

                await Task.Yield();   // continuation runs on the thread pool, off the reactor

                Drain(conn, snapshot);
                conn.Write(Ok);
                await conn.FlushAsync();

                if (snapshot.IsClosed) return;
                conn.ResetRead();
            }
        }
        finally
        {
            conn.DecRef();
        }
    }

    /// <summary>
    /// pg - each request runs a query through the reactor's pool; a server error becomes a 500.
    /// Paths: / → SELECT 42 · /sleep → 100ms query (pool concurrency demo) · /err → server error.
    /// </summary>
    public static async Task Pg(Reactor reactor, Connection conn)
    {
        PgPool pool = reactor.GetService<PgPool>();

        try
        {
            while (true)
            {
                RecvSnapshot snapshot = await conn.ReadAsync();
                string path = ReadRequestPath(conn, snapshot);

                string sql = path switch
                {
                    "/sleep" => "SELECT 42 FROM pg_sleep(0.1)",
                    "/err"   => "SELECT * FROM this_table_does_not_exist",
                    _        => "SELECT 42",
                };

                try
                {
                    PgResult result = await pool.QueryAsync(sql);
                    WriteDbResponse(conn, result.Value ?? "");
                }
                catch (PgException e)
                {
                    Console.Error.WriteLine($"[pg] query failed: {e.Message}");
                    conn.Write(ServerError);
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
    }

    /// <summary>
    /// file - static files over the shared asset cache: small assets served from the snapshot's
    /// baked response, large ones read off the ring through a rented reader; misses are 404.
    /// </summary>
    public static async Task File(Reactor reactor, Connection conn)
    {
        StaticAssets assets = reactor.GetService<StaticAssets>();
        RingPool<AssetReader> readers = reactor.GetService<RingPool<AssetReader>>();

        try
        {
            while (true)
            {
                RecvSnapshot snapshot = await conn.ReadAsync();

                if (!FindAsset(conn, snapshot, assets, out AssetCache.Asset asset))
                {
                    conn.Write(NotFound);
                    await conn.FlushAsync();
                }
                else if (asset.Response != 0)
                {
                    // Hot path: the whole response (header + body) is baked into the
                    // snapshot - no file I/O, no header formatting, no allocation.
                    await SendChunked(conn, asset.Response, asset.ResponseLength);
                }
                else
                {
                    AssetReader reader = await readers.RentAsync();
                    try
                    {
                        int read = await reader.ReadAsync(asset.Fd, offset: 0);
                        if (read >= 0)
                        {
                            WriteAssetHeader(conn, asset, read);
                            await SendChunked(conn, reader.Buffer, read);
                        }
                        else
                        {
                            // e.g. the descriptor went stale across a reload race
                            conn.Write(ServerError);
                            await conn.FlushAsync();
                        }
                    }
                    finally
                    {
                        readers.Return(reader);
                    }
                }

                if (snapshot.IsClosed) return;
                conn.ResetRead();
            }
        }
        finally
        {
            conn.DecRef();
        }
    }

    // Drain the recv (the raw/pg handlers don't parse the request).
    private static void Drain(Connection conn, RecvSnapshot snapshot)
    {
        while (conn.TryGetItem(snapshot, out SpscRecvRing.Item item))
        {
            if (item.HasBuffer)
            {
                conn.ReturnBuffer(in item);
            }
        }
    }

    // Drain the recv and return the request target path (defaults to "/").
    private static string ReadRequestPath(Connection conn, RecvSnapshot snapshot)
    {
        string path = "/";

        while (conn.TryGetItem(snapshot, out SpscRecvRing.Item item))
        {
            if (item.HasBuffer)
            {
                string? parsed = ParsePath(item.AsSpan());
                if (parsed != null) path = parsed;

                conn.ReturnBuffer(in item);
            }
        }

        return path;
    }

    // Pull the target out of a request line: "GET /css/app.css?v=1 HTTP/1.1" -> "/css/app.css".
    private static string? ParsePath(ReadOnlySpan<byte> request)
    {
        return TryParseTarget(request, out ReadOnlySpan<byte> target)
            ? Encoding.ASCII.GetString(target)
            : null;
    }

    private static bool TryParseTarget(ReadOnlySpan<byte> request, out ReadOnlySpan<byte> target)
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

    // Drain the recv, resolving the request target against the cache while the bytes are still
    // valid (the lookup is span-based - no string - so it must happen before the buffer goes
    // back to the ring).
    private static bool FindAsset(Connection conn, RecvSnapshot snapshot, StaticAssets assets, out AssetCache.Asset asset)
    {
        bool found = false;
        asset = default;

        while (conn.TryGetItem(snapshot, out SpscRecvRing.Item item))
        {
            if (item.HasBuffer)
            {
                if (!found && TryParseTarget(item.AsSpan(), out ReadOnlySpan<byte> target))
                {
                    found = assets.TryGet(target, out asset);
                }

                conn.ReturnBuffer(in item);
            }
        }

        return found;
    }

    // Copy native memory through the write slab in chunks and flush - one flush for small
    // payloads, a short sequence for ones bigger than the slab.
    private const int BodyChunk = 12 * 1024;

    private static async Task SendChunked(Connection conn, nint data, int length)
    {
        int sent = 0;
        while (true)
        {
            int chunk = Math.Min(length - sent, BodyChunk);
            WriteBodyChunk(conn, data + sent, chunk);
            await conn.FlushAsync();
            sent += chunk;

            if (sent >= length) return;
        }
    }

    private static void WriteAssetHeader(Connection conn, AssetCache.Asset asset, int bodyLength)
    {
        Span<byte> header = stackalloc byte[256];
        conn.Write(header[..AssetCache.WriteResponseHeader(header, asset.Path, bodyLength)]);
    }

    private static unsafe void WriteBodyChunk(Connection conn, nint chunk, int length)
    {
        conn.Write(new ReadOnlySpan<byte>((void*)chunk, length));
    }

    private static void WriteDbResponse(Connection conn, string value)
    {
        Span<byte> response = stackalloc byte[160];
        int position = 0;

        position += Copy(response[position..], "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: "u8);

        int bodyLength = 3 + value.Length;   // "db=" + value
        Utf8Formatter.TryFormat(bodyLength, response[position..], out int digits);
        position += digits;

        position += Copy(response[position..], "\r\n\r\ndb="u8);
        position += Encoding.ASCII.GetBytes(value, response[position..]);

        conn.Write(response[..position]);
    }

    private static int Copy(Span<byte> destination, ReadOnlySpan<byte> source)
    {
        source.CopyTo(destination);
        return source.Length;
    }

    /// <summary>Create a small sample asset directory if it's missing, so `file` mode has something to serve.</summary>
    public static string EnsureSampleDir(string dir)
    {
        Directory.CreateDirectory(dir);

        string index = Path.Combine(dir, "index.html");
        if (!System.IO.File.Exists(index))
        {
            System.IO.File.WriteAllText(index,
                "<!doctype html><html><head><title>ioxide</title><link rel=stylesheet href=/style.css></head>" +
                "<body><h1>Served from disk via io_uring</h1><p>Read off the reactor's ring - no thread pool.</p></body></html>");
        }

        string css = Path.Combine(dir, "style.css");
        if (!System.IO.File.Exists(css))
        {
            System.IO.File.WriteAllText(css, "body{font-family:system-ui;margin:3rem;color:#222}h1{color:#06c}");
        }

        return dir;
    }
}
