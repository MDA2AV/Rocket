using System.Buffers.Text;
using System.Text;
using ioxide;
using ioxide.file;
using ioxide.pg;
using ioxide.utils;

namespace Playground;

/// <summary>
/// The three request handlers the Playground can serve. Each is the same connection loop — read,
/// respond, flush, repeat until the client closes — differing only in how it produces the response.
/// </summary>
internal static class Handlers
{
    private static ReadOnlySpan<byte> Ok =>
        "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 2\r\n\r\nok"u8;

    private static ReadOnlySpan<byte> NotFound =>
        "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\n\r\n"u8;

    /// <summary>raw — a fixed plaintext response; no I/O beyond the socket.</summary>
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

    /// <summary>pg — answer each request with "SELECT 42", run over the reactor's own ring.</summary>
    public static async Task Pg(Reactor reactor, Connection conn)
    {
        var db = (PgConnection)reactor.State!;

        try
        {
            while (true)
            {
                RecvSnapshot snapshot = await conn.ReadAsync();
                Drain(conn, snapshot);

                string answer = await db.QueryAsync("SELECT 42");
                WriteDbResponse(conn, answer);
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
    /// file — a static file server. The requested URL path is looked up in the shared asset cache;
    /// a hit is read off the ring (the Fractal model — an io_uring read on the reactor, resumed
    /// inline) and served with its content type; a miss is a 404.
    /// </summary>
    public static async Task File(Reactor reactor, Connection conn)
    {
        var endpoint = (FileEndpoint)reactor.State!;

        try
        {
            while (true)
            {
                RecvSnapshot snapshot = await conn.ReadAsync();
                string path = ReadRequestPath(conn, snapshot);

                if (endpoint.Assets.TryGet(path, out AssetCache.Asset asset))
                {
                    int read = await endpoint.Reader.ReadAsync(asset.Fd, endpoint.Buffer, endpoint.BufferSize, offset: 0);
                    WriteAsset(conn, asset, endpoint.Buffer, read);
                }
                else
                {
                    conn.Write(NotFound);
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

    // ── helpers ─────────────────────────────────────────────────────────────

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
        int firstSpace = request.IndexOf((byte)' ');
        if (firstSpace < 0) return null;

        ReadOnlySpan<byte> afterMethod = request[(firstSpace + 1)..];
        int secondSpace = afterMethod.IndexOf((byte)' ');
        if (secondSpace < 0) return null;

        ReadOnlySpan<byte> target = afterMethod[..secondSpace];

        int query = target.IndexOf((byte)'?');
        if (query >= 0) target = target[..query];

        return Encoding.ASCII.GetString(target);
    }

    private static unsafe void WriteAsset(Connection conn, AssetCache.Asset asset, nint buffer, int bodyLength)
    {
        Span<byte> header = stackalloc byte[256];
        int position = 0;

        position += Copy(header[position..], "HTTP/1.1 200 OK\r\nContent-Type: "u8);
        position += Copy(header[position..], MimeFor(asset.Path));
        position += Copy(header[position..], "\r\nContent-Length: "u8);
        Utf8Formatter.TryFormat(bodyLength, header[position..], out int digits);
        position += digits;
        position += Copy(header[position..], "\r\n\r\n"u8);

        conn.Write(header[..position]);
        conn.Write(new ReadOnlySpan<byte>((void*)buffer, bodyLength));
    }

    private static ReadOnlySpan<byte> MimeFor(string path) => Path.GetExtension(path) switch
    {
        ".html" => "text/html"u8,
        ".css"  => "text/css"u8,
        ".js"   => "application/javascript"u8,
        ".json" => "application/json"u8,
        ".svg"  => "image/svg+xml"u8,
        ".png"  => "image/png"u8,
        ".webp" => "image/webp"u8,
        ".woff2" => "font/woff2"u8,
        _       => "application/octet-stream"u8
    };

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
                "<body><h1>Served from disk via io_uring</h1><p>Read off the reactor's ring — no thread pool.</p></body></html>");
        }

        string css = Path.Combine(dir, "style.css");
        if (!System.IO.File.Exists(css))
        {
            System.IO.File.WriteAllText(css, "body{font-family:system-ui;margin:3rem;color:#222}h1{color:#06c}");
        }

        return dir;
    }
}
