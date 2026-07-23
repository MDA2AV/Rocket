using System.Buffers.Text;
using System.IO.Pipelines;
using System.Text;
using Microsoft.Win32.SafeHandles;
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
    public static async Task Raw(Reactor reactor, TcpConnection conn)
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
    public static async Task Pipe(Reactor reactor, TcpConnection conn)
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
    public static async Task Hop(Reactor reactor, TcpConnection conn)
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

    private static ReadOnlySpan<byte> JsonHeader =>
        "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 13\r\n\r\n"u8;

    private static int _offReactorSeen;

    /// <summary>
    /// taskrun - raw, but each request awaits a Task.Run JSON serialization. With the reactor
    /// SynchronizationContext installed the continuation comes home to the reactor; without it,
    /// it stays on the thread pool. Logs once if the post-await thread is off-reactor.
    /// </summary>
    public static async Task TaskRun(Reactor reactor, TcpConnection conn)
    {
        try
        {
            while (true)
            {
                RecvSnapshot snapshot = await conn.ReadAsync();
                Drain(conn, snapshot);

                string json = System.Text.Json.JsonSerializer.Serialize("hello world");
                //string json = await Task.Run(() => System.Text.Json.JsonSerializer.Serialize("hello world"));

                if (!reactor.OnReactorThread && Interlocked.Exchange(ref _offReactorSeen, 1) == 0)
                {
                    Console.WriteLine("[taskrun] continuation resumed OFF the reactor (no sync context)");
                }

                conn.Write(JsonHeader);
                conn.Write(Encoding.UTF8.GetBytes(json));
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
    public static async Task Pg(Reactor reactor, TcpConnection conn)
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
                    "/hang"  => "SELECT pg_sleep(10)",
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
    public static async Task File(Reactor reactor, TcpConnection conn)
    {
        StaticAssets assets = reactor.GetService<StaticAssets>();
        RingPool<AssetReader> readers = reactor.GetService<RingPool<AssetReader>>();

        try
        {
            while (true)
            {
                RecvSnapshot snapshot = await conn.ReadAsync();

                // Hold the snapshot for the whole request so a concurrent reload can't free the fd
                // or baked response out from under an in-flight read/send.
                using (StaticAssets.Lease lease = assets.Acquire())
                {
                    if (!FindAsset(conn, snapshot, lease, out AssetCache.Asset asset))
                    {
                        conn.Write(NotFound);
                        await conn.FlushAsync();
                    }
                    else
                    {
                        // Revalidate against disk (size + mtime + inode). The baked response is the
                        // hot path only while the file is unchanged; an edit or atomic rename is
                        // served live instead, so RAM never goes stale. (Re-bakes on Reload().)
                        bool fresh = AssetCache.IsFresh(asset, out bool exists, out long size);
                        if (!exists)
                        {
                            conn.Write(NotFound);                                            // vanished
                            await conn.FlushAsync();
                        }
                        else if (fresh && asset.Response != 0)
                        {
                            await SendChunked(conn, asset.Response, asset.ResponseLength);    // baked hot path
                        }
                        else if (fresh)
                        {
                            await SendFromDisk(conn, readers, asset, asset.Fd, asset.Length); // large, unchanged
                        }
                        else
                        {
                            await SendChangedFromDisk(conn, readers, asset, size);            // changed -> live
                        }
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

    // Stream an asset off the ring from <paramref name="fd"/>, framing Content-Length from
    // <paramref name="totalLength"/>. Files bigger than the reader's buffer are read in successive
    // chunks at advancing offsets, so they're served whole instead of truncated.
    private static async Task SendFromDisk(TcpConnection conn, RingPool<AssetReader> readers, AssetCache.Asset asset, int fd, long totalLength)
    {
        AssetReader reader = await readers.RentAsync();
        try
        {
            int first = await reader.ReadAsync(fd, offset: 0);
            if (first < 0)
            {
                conn.Write(ServerError);
                await conn.FlushAsync();
                return;
            }

            WriteAssetHeader(conn, asset, (int)totalLength);   // full length up front
            await SendChunked(conn, reader.Buffer, first);

            long offset = first;
            while (offset < totalLength)
            {
                int read = await reader.ReadAsync(fd, offset);
                if (read <= 0)
                {
                    break;   // EOF or mid-stream error; the response is already committed
                }
                await SendChunked(conn, reader.Buffer, read);
                offset += read;
            }
        }
        finally
        {
            readers.Return(reader);
        }
    }

    // Serve a file whose on-disk version no longer matches the baked snapshot: open the current path
    // fresh (so an atomic rename resolves to the new inode, not the cached fd) and stream it live.
    private static async Task SendChangedFromDisk(TcpConnection conn, RingPool<AssetReader> readers, AssetCache.Asset asset, long size)
    {
        SafeFileHandle handle;
        try
        {
            handle = System.IO.File.OpenHandle(asset.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch
        {
            conn.Write(NotFound);
            await conn.FlushAsync();
            return;
        }

        try
        {
            int fd = (int)handle.DangerousGetHandle();
            await SendFromDisk(conn, readers, asset, fd, size);
        }
        finally
        {
            handle.Dispose();
        }
    }

    // Drain the recv (the raw/pg handlers don't parse the request).
    private static void Drain(TcpConnection conn, RecvSnapshot snapshot)
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
    private static string ReadRequestPath(TcpConnection conn, RecvSnapshot snapshot)
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
    private static bool FindAsset(TcpConnection conn, RecvSnapshot snapshot, StaticAssets.Lease lease, out AssetCache.Asset asset)
    {
        bool found = false;
        asset = default;

        while (conn.TryGetItem(snapshot, out SpscRecvRing.Item item))
        {
            if (item.HasBuffer)
            {
                if (!found && TryParseTarget(item.AsSpan(), out ReadOnlySpan<byte> target))
                {
                    found = lease.TryGet(target, out asset);
                }

                conn.ReturnBuffer(in item);
            }
        }

        return found;
    }

    // Copy native memory through the write slab in chunks and flush - one flush for small
    // payloads, a short sequence for ones bigger than the slab.
    private const int BodyChunk = 12 * 1024;

    private static async Task SendChunked(TcpConnection conn, nint data, int length)
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

    private static void WriteAssetHeader(TcpConnection conn, AssetCache.Asset asset, int bodyLength)
    {
        Span<byte> header = stackalloc byte[256];
        conn.Write(header[..AssetCache.WriteResponseHeader(header, asset.Path, bodyLength)]);
    }

    private static unsafe void WriteBodyChunk(TcpConnection conn, nint chunk, int length)
    {
        conn.Write(new ReadOnlySpan<byte>((void*)chunk, length));
    }

    private static void WriteDbResponse(TcpConnection conn, string value)
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
