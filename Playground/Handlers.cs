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
        var reader = new TcpConnectionPipeReader(conn);
        var writer = new TcpConnectionPipeWriter(conn);

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

    /// <summary>
    /// HTTP/3 via ioxide.nghttp3, STREAMING flavor: requests dispatch at end-of-headers, and
    /// POST /upload pulls its body through <c>req.BodyReader</c> chunk by chunk while the
    /// stream's flow-control window paces the peer (memory bound = one window, not the body
    /// size). Everything else answers hello. Try it:
    ///
    ///   h2load --alpn-list=h3 -n 1 -c 1 -d bigfile.bin https://127.0.0.1:8443/upload
    /// </summary>
    // QPACK dynamic table knob for the h3 modes: PLAYGROUND_QPACK_CAP=4096 advertises a
    // decode-side table (blocked streams 100); unset/0 = static-only, nghttp3's default.
    private static readonly ioxide.nghttp3.Nghttp3Options Nghttp3Opts = BuildNghttp3Options();

    private static ioxide.nghttp3.Nghttp3Options BuildNghttp3Options()
    {
        long capacity = long.TryParse(Environment.GetEnvironmentVariable("PLAYGROUND_QPACK_CAP"), out long parsed) ? parsed : 0;
        return new ioxide.nghttp3.Nghttp3Options
        {
            QpackDynamicTableCapacity = capacity,
            QpackBlockedStreams = capacity > 0 ? 100 : 0,
        };
    }

    // Live h3 connections, so a SIGTERM can GOAWAY them all (see Program.cs). Each reactor only
    // ever adds its own, but a plain lock keeps the signal handler - which runs off-reactor -
    // honest.
    private static readonly List<(Reactor Reactor, ioxide.nghttp3.Nghttp3Connection Connection)> LiveH3 = [];

    /// <summary>Graceful drain: GOAWAY every live h3 connection (called from the SIGTERM handler,
    /// i.e. OFF the reactor threads - so each Shutdown is marshalled onto its owning reactor,
    /// which is where nghttp3 and the send path must be touched). Each connection finishes its
    /// in-flight requests, then closes itself.</summary>
    public static void ShutdownAllH3()
    {
        lock (LiveH3)
        {
            foreach ((Reactor reactor, ioxide.nghttp3.Nghttp3Connection connection) in LiveH3)
            {
                reactor.ScheduleOnReactor(static state => ((ioxide.nghttp3.Nghttp3Connection)state!).Shutdown(), connection);
            }
            LiveH3.Clear();
        }
    }

    private static ioxide.nghttp3.Nghttp3Connection TrackH3(Reactor reactor, ioxide.nghttp3.Nghttp3Connection connection)
    {
        lock (LiveH3)
        {
            LiveH3.Add((reactor, connection));
        }
        return connection;
    }

    // Field names/values reused across responses: static byte literals, so a response that
    // uses only these allocates nothing beyond the response object itself.
    private static readonly byte[] ContentType   = "content-type"u8.ToArray();
    private static readonly byte[] TextPlain     = "text/plain"u8.ToArray();
    private static readonly byte[] SetCookie     = "set-cookie"u8.ToArray();
    private static readonly byte[] ServerName    = "server"u8.ToArray();
    private static readonly byte[] ServerValue   = "ioxide"u8.ToArray();
    private static readonly byte[] SessionCookie = "session=demo; Path=/; HttpOnly; SameSite=Lax"u8.ToArray();

    /// <summary>
    /// The allocation-free response pattern: build it ONCE and reuse the instance for every
    /// request. Legal because the h3 layer copies status, headers and body into nghttp3
    /// synchronously at submit and never retains the object - so a static response costs zero
    /// allocations per request, unlike <c>Nghttp3Response.Text($"...")</c> which encodes a fresh
    /// string every time. This is what a hot path should look like.
    /// </summary>
    private static readonly ioxide.nghttp3.Nghttp3Response PlaintextResponse = BuildPlaintextResponse();

    private static ioxide.nghttp3.Nghttp3Response BuildPlaintextResponse()
    {
        var response = new ioxide.nghttp3.Nghttp3Response { Body = "Hello, World!"u8.ToArray() };
        response.Headers.Add(ContentType, TextPlain);
        response.Headers.Add(ServerName, ServerValue);
        return response;
    }

    /// <summary>
    /// HTTP/3 via ioxide.nghttp3, STREAMING flavor - dispatch at end-of-headers, bodies pulled
    /// under flow-control pacing. Routes, each demonstrating one part of the byte-level surface:
    ///
    ///   /plaintext  static response, zero allocations per request
    ///   /upload     streamed request body (memory bound = one window, not the body size)
    ///   /headers    walks req.Headers.AsSpan() - the KeyValueList, no strings
    ///   /cookies    req.TryGetCookie + req.Cookies, and sets one back via set-cookie
    ///   anything    hello, decoding the path only because it goes into text
    /// </summary>
    public static Task Nghttp3Streamed(Reactor reactor, QuicConnection conn)
    {
        return TrackH3(reactor, new ioxide.nghttp3.Nghttp3Connection(conn, Nghttp3Opts)).RunStreamingAsync(static async request =>
        {
            ReadOnlySpan<byte> path = request.Path.Span;

            // Byte-compare routing: no decode, no allocation, no dictionary.
            if (path.SequenceEqual("/plaintext"u8))
            {
                return PlaintextResponse;
            }

            if (path.SequenceEqual("/upload"u8))
            {
                // The handler runs while the body is still arriving; each chunk read credits the
                // peer's flow-control window, so a slow consumer throttles the sender instead of
                // buffering. Chunks are valid until the next ReadAsync.
                long total = 0;
                while (true)
                {
                    ReadOnlyMemory<byte> chunk = await request.BodyReader!.ReadAsync();
                    if (chunk.IsEmpty)
                    {
                        break;
                    }
                    total += chunk.Length;   // a real app would parse/store the chunk here
                }

                return ioxide.nghttp3.Nghttp3Response.Text($"received {total} bytes (streamed) over HTTP/3\n");
            }

            if (path.SequenceEqual("/headers"u8))
            {
                // KeyValueList: ordered, duplicate-preserving, enumerated over a span. Names are
                // lowercase on the wire (h3 requires it), values are raw octets.
                var report = new System.Text.StringBuilder();
                report.Append($"{request.Headers.Count} header field lines\n");
                foreach ((ReadOnlyMemory<byte> name, ReadOnlyMemory<byte> value) in request.Headers.AsSpan())
                {
                    report.Append($"  {System.Text.Encoding.ASCII.GetString(name.Span)}: "
                                + $"{System.Text.Encoding.ASCII.GetString(value.Span)}\n");
                }
                return ioxide.nghttp3.Nghttp3Response.Text(report.ToString());
            }

            if (path.SequenceEqual("/cookies"u8))
            {
                // One-shot lookup, byte-level - the common case.
                string session = request.TryGetCookie("session"u8, out ReadOnlyMemory<byte> value)
                    ? System.Text.Encoding.ASCII.GetString(value.Span)
                    : "(none)";

                // Enumerating walks every cookie field line: h3 may split one logical cookie
                // header across several, which a plain header lookup would miss.
                int count = 0;
                foreach ((ReadOnlyMemory<byte> _, ReadOnlyMemory<byte> _) in request.Cookies)
                {
                    count++;
                }

                var response = new ioxide.nghttp3.Nghttp3Response
                {
                    Body = System.Text.Encoding.UTF8.GetBytes($"session={session}, {count} cookie(s) sent\n"),
                };
                response.Headers.Add(ContentType, TextPlain);
                response.Headers.Add(SetCookie, SessionCookie);   // repeat the Add for more cookies
                return response;
            }

            return ioxide.nghttp3.Nghttp3Response.Text(
                $"hello {System.Text.Encoding.ASCII.GetString(path)} over HTTP/3 via io_uring\n");
        });
    }

    /// <summary>
    /// HTTP/3 via ioxide.nghttp3, BUFFERED-ASYNC flavor: dispatch waits for end-of-stream, so the
    /// WHOLE body is already in <c>request.Body</c> - no BodyReader, no pacing - while the handler
    /// may still await (a PgPool query, Redis, any ioxide-native awaitable resumes inline on the
    /// reactor). The trade: memory holds the entire body, so it suits normal-sized requests; use
    /// the streamed flavor when uploads can be large or hostile.
    /// </summary>
    public static Task Nghttp3Buffered(Reactor reactor, QuicConnection conn)
    {
        return TrackH3(reactor, new ioxide.nghttp3.Nghttp3Connection(conn, Nghttp3Opts)).RunBufferedAsync(static async request =>
        {
            ReadOnlySpan<byte> path = request.Path.Span;

            if (path.SequenceEqual("/plaintext"u8))
            {
                return PlaintextResponse;
            }

            if (path.SequenceEqual("/upload"u8))
            {
                // Complete before we run: Length is a property read, and the bytes are all here.
                // This is where a real await (storing request.Body, say) would slot in.
                await ValueTask.CompletedTask;
                return ioxide.nghttp3.Nghttp3Response.Text(
                    $"received {request.Body.Length} bytes (buffered) over HTTP/3\n");
            }

            return ioxide.nghttp3.Nghttp3Response.Text(
                $"hello {System.Text.Encoding.ASCII.GetString(path)} over HTTP/3 via io_uring\n");
        });
    }

    /// <summary>
    /// The pure-C# HTTP/3 stack (ioxide.http3: frames + QPACK + Huffman, no native h3 code) on
    /// the same streaming surface as <see cref="H3"/> - POST /upload pulls the body chunk by
    /// chunk under flow-control pacing, everything else answers hello.
    /// </summary>
    public static Task Http3(Reactor reactor, QuicConnection conn)
        => new ioxide.http3.Http3Connection(conn).RunAsync(
            static async req =>
            {
                if (req.Path.Span.SequenceEqual("/upload"u8))
                {
                    long total = 0;
                    while (true)
                    {
                        ReadOnlyMemory<byte> chunk = await req.BodyReader!.ReadAsync();
                        if (chunk.IsEmpty)
                        {
                            break;
                        }
                        total += chunk.Length;
                    }
                    return ioxide.http3.Http3Response.Text($"received {total} bytes over pure-C# HTTP/3\n");
                }

                return ioxide.http3.Http3Response.Text($"hello {System.Text.Encoding.ASCII.GetString(req.Path.Span)} over pure-C# HTTP/3\n");
            });

    /// <summary>
    /// Reverse proxy: every inbound request is forwarded to an upstream origin through the
    /// ring-native HTTP client, and the upstream's status and body are relayed back. Both hops -
    /// the inbound connection and the outbound call - run on this reactor's ring and resume
    /// inline, so a proxied request never leaves the thread it arrived on.
    ///
    ///   PLAYGROUND_UPSTREAM_HOST / _PORT point at the origin (default 127.0.0.1:8081)
    ///   PLAYGROUND_UPSTREAM_POOL sizes the connection pool per reactor (default 8)
    /// </summary>
    public static async Task Proxy(Reactor reactor, TcpConnection conn)
    {
        try
        {
            ioxide.httpclient.HttpClientPool upstream = reactor.GetService<ioxide.httpclient.HttpClientPool>()!;

            while (true)
            {
                RecvSnapshot snapshot = await conn.ReadAsync();
                string path = ReadRequestPath(conn, snapshot);

                try
                {
                    using ioxide.httpclient.HttpClientResponse response = await upstream.GetAsync(path);
                    conn.Write(System.Text.Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 {response.Status} X\r\nContent-Length: {response.Body.Length}\r\n\r\n"));
                    conn.Write(response.Body.Span);
                }
                catch (ioxide.httpclient.HttpClientException e)
                {
                    byte[] message = System.Text.Encoding.ASCII.GetBytes($"upstream: {e.Message}");
                    conn.Write(System.Text.Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 502 Bad Gateway\r\nContent-Length: {message.Length}\r\n\r\n"));
                    conn.Write(message);
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

    /// <summary>Upstream options for the proxy mode.</summary>
    public static ioxide.httpclient.HttpClientOptions UpstreamOptions() => new()
    {
        Host = Environment.GetEnvironmentVariable("PLAYGROUND_UPSTREAM_HOST") ?? "127.0.0.1",
        Port = ushort.TryParse(Environment.GetEnvironmentVariable("PLAYGROUND_UPSTREAM_PORT"), out ushort port) ? port : (ushort)8081,
        PoolSize = int.TryParse(Environment.GetEnvironmentVariable("PLAYGROUND_UPSTREAM_POOL"), out int pool) ? pool : 8,
    };

    /// <summary>Self-signed localhost cert for the quic mode (PLAYGROUND_QUIC_CERT/KEY override it).</summary>
    public static (string CertPath, string KeyPath) EnsureQuicCert()
    {
        string? envCert = Environment.GetEnvironmentVariable("PLAYGROUND_QUIC_CERT");
        string? envKey = Environment.GetEnvironmentVariable("PLAYGROUND_QUIC_KEY");
        if (envCert is not null && envKey is not null)
        {
            return (envCert, envKey);
        }

        string dir = Path.Combine(Path.GetTempPath(), "ioxide-playground-quic");
        Directory.CreateDirectory(dir);
        string certPath = Path.Combine(dir, "quic.crt");
        string keyPath = Path.Combine(dir, "quic.key");

        if (!System.IO.File.Exists(certPath))
        {
            using var rsa = System.Security.Cryptography.RSA.Create(2048);
            var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(
                "CN=localhost", rsa, System.Security.Cryptography.HashAlgorithmName.SHA256,
                System.Security.Cryptography.RSASignaturePadding.Pkcs1);
            using var cert = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

            System.IO.File.WriteAllText(certPath, cert.ExportCertificatePem());
            System.IO.File.WriteAllText(keyPath, rsa.ExportPkcs8PrivateKeyPem());
        }

        return (certPath, keyPath);
    }
}
