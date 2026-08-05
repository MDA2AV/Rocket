using System.Text;
using ioxide;
using ioxide.file;
using ioxide.tls;
using ioxide.utils;

namespace Ioxide.Tests;

/// <summary>Server-side HTTP helpers for the test handlers (local copy so the suite depends only on the modules).</summary>
public static class Wire
{
    public static string ReadPath(TcpConnection conn, RecvSnapshot snapshot)
    {
        string path = "/";

        while (conn.TryGetItem(snapshot, out SpscRecvRing.Item item))
        {
            if (item.HasBuffer)
            {
                if (path == "/")
                {
                    path = ParsePath(item.AsSpan());
                }

                conn.ReturnBuffer(in item);
            }
        }

        return path;
    }

    public static void Write(TcpConnection conn, int status, string body)
    {
        conn.Write(Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} X\r\nContent-Type: text/plain\r\nContent-Length: {body.Length}\r\n\r\n{body}"));
    }

    private static string ParsePath(ReadOnlySpan<byte> request)
    {
        int firstSpace = request.IndexOf((byte)' ');
        if (firstSpace < 0)
        {
            return "/";
        }

        ReadOnlySpan<byte> rest = request[(firstSpace + 1)..];
        int secondSpace = rest.IndexOf((byte)' ');
        ReadOnlySpan<byte> target = secondSpace > 0 ? rest[..secondSpace] : rest;
        return Encoding.ASCII.GetString(target);
    }
}

/// <summary>The per-module test handlers, each routing that module's feature surface by path.</summary>
public static class Handlers
{
    // core: echo "ok" - exercises accept, the buffer-ring recv, send, and the keep-alive read loop.
    public static async Task Raw(Reactor r, TcpConnection conn)
    {
        try
        {
            while (true)
            {
                RecvSnapshot snapshot = await conn.ReadAsync();
                Wire.ReadPath(conn, snapshot);
                Wire.Write(conn, 200, "ok");
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

    // pg: /add/N (int param), /rows (streaming), /bad (server error, caught), else SELECT 42.

    // pg with a query longer than the (short) command timeout - for the timeout test.

    // redis: /incr (RESP integer), /pipe (SET+INCR+GET in one round trip), else SET then GET.

    // file: serve a baked asset by path; 404 on miss.
    public static async Task Files(Reactor r, TcpConnection conn)
    {
        StaticAssets assets = r.GetService<StaticAssets>();

        try
        {
            while (true)
            {
                RecvSnapshot snapshot = await conn.ReadAsync();
                string path = Wire.ReadPath(conn, snapshot);

                using (StaticAssets.Lease lease = assets.Acquire())
                {
                    if (lease.TryGet(path, out AssetCache.Asset asset) && asset.Response != 0)
                    {
                        WriteNative(conn, asset.Response, asset.ResponseLength);
                    }
                    else
                    {
                        Wire.Write(conn, 404, "missing");
                    }
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

    // tls: kTLS handshake, then a fixed plaintext response the kernel encrypts on send.
    public static async Task Tls(Reactor r, TcpConnection conn)
    {
        TlsSession? tls = null;

        try
        {
            tls = await r.GetService<TlsService>().AcceptAsync(conn);

            while (true)
            {
                RecvSnapshot snapshot = await conn.ReadAsync();

                int got = 0;
                while (conn.TryGetItem(snapshot, out SpscRecvRing.Item item))
                {
                    if (item.HasBuffer)
                    {
                        got += DecryptLength(tls, item);
                        conn.ReturnBuffer(in item);
                    }
                }

                if (got > 0)
                {
                    Wire.Write(conn, 200, "tls-ok");
                    await conn.FlushAsync();
                }

                if (snapshot.IsClosed || tls.Closed)
                {
                    return;
                }

                conn.ResetRead();
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[tls-test] handler: {e.Message}");
        }
        finally
        {
            tls?.Dispose();
            conn.DecRef();
        }
    }

    private static unsafe int DecryptLength(TlsSession tls, in SpscRecvRing.Item item)
    {
        return tls.Decrypt(item.Ptr, item.Len).Length;
    }

    // The asset cache hands back one native response block; copy it through the write slab.
    private static unsafe void WriteNative(TcpConnection conn, nint data, int length)
    {
        conn.Write(new ReadOnlySpan<byte>((void*)data, length));
    }
}
