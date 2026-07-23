using ioxide;
using ioxide.file;

using Microsoft.Win32.SafeHandles;

namespace Examples.Files;

/// <summary>
/// Static files over the shared asset cache. A <see cref="StaticAssets.Lease"/> keeps the snapshot
/// alive for the whole request (so a concurrent reload can't free the fd mid-send). Every lookup is
/// revalidated against disk (size + mtime + inode): an unchanged small file is served from its baked
/// HTTP response with no I/O; a file that was edited or atomically replaced is opened fresh and
/// streamed live, so the cache never serves stale bytes (it re-bakes on the next reload). Larger
/// files are read off the ring in chunks. Misses are 404.
/// </summary>
public static class StaticExample
{
    private const int Chunk = 12 * 1024;

    public static async Task Handle(Reactor r, TcpConnection conn)
    {
        StaticAssets assets = r.GetService<StaticAssets>();
        RingPool<AssetReader> readers = r.GetService<RingPool<AssetReader>>();

        try
        {
            while (true)
            {
                RecvSnapshot snapshot = await conn.ReadAsync();
                string path = Http.ReadPath(conn, snapshot);

                using (StaticAssets.Lease lease = assets.Acquire())
                {
                    if (!lease.TryGet(path, out AssetCache.Asset asset))
                    {
                        await NotFound(conn, path);
                    }
                    else
                    {
                        // Revalidate the cached asset against disk. The baked response is the hot path
                        // only while the file is unchanged; an edit or atomic rename is served live, so
                        // RAM never goes stale.
                        bool fresh = AssetCache.IsFresh(asset, out bool exists, out long size);
                        if (!exists)
                        {
                            await NotFound(conn, path);                                       // vanished
                        }
                        else if (fresh && asset.Response != 0)
                        {
                            await SendNative(conn, asset.Response, asset.ResponseLength);      // baked hot path
                        }
                        else if (fresh)
                        {
                            await SendFromDisk(conn, readers, asset, asset.Fd, asset.Length);  // large, unchanged
                        }
                        else
                        {
                            await SendChangedFromDisk(conn, readers, asset, size);             // changed -> live
                        }
                    }
                }

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

    private static async Task NotFound(TcpConnection conn, string path)
    {
        Http.WriteText(conn, 404, "Not Found", $"no asset {path}");
        await conn.FlushAsync();
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
                Http.WriteText(conn, 500, "Internal Server Error", "read failed");
                await conn.FlushAsync();
                return;
            }

            WriteHeader(conn, asset, totalLength);
            await SendNative(conn, reader.Buffer, first);

            long offset = first;
            while (offset < totalLength)
            {
                int read = await reader.ReadAsync(fd, offset);
                if (read <= 0)
                {
                    break;
                }

                await SendNative(conn, reader.Buffer, read);
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
            await NotFound(conn, asset.Path);
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

    // Format the 200 header into a stack buffer (kept in a sync method - a Span can't cross an await).
    private static void WriteHeader(TcpConnection conn, AssetCache.Asset asset, long bodyLength)
    {
        Span<byte> header = stackalloc byte[256];
        conn.Write(header[..AssetCache.WriteResponseHeader(header, asset.Path, (int)bodyLength)]);
    }

    // Copy native memory through the write slab in slab-sized chunks and flush each.
    private static async Task SendNative(TcpConnection conn, nint data, int length)
    {
        int sent = 0;

        while (sent < length)
        {
            int n = Math.Min(length - sent, Chunk);
            WriteChunk(conn, data + sent, n);
            await conn.FlushAsync();
            sent += n;
        }
    }

    private static unsafe void WriteChunk(TcpConnection conn, nint chunk, int length)
    {
        conn.Write(new ReadOnlySpan<byte>((void*)chunk, length));
    }
}
