using Microsoft.Win32.SafeHandles;
using ioxide;
using ioxide.file;
using Playground.Http;

namespace Playground.Handlers;

/// <summary>
/// file - static files over the shared asset cache: small assets served from the snapshot's baked
/// response, large ones read off the ring through a rented reader; misses are 404.
/// </summary>
internal static class FileHandler
{
    public static Task Handle(Reactor reactor, TcpConnection conn)
        => ConnectionLoop.ServeAsync(
            conn,
            new FileResponder(reactor.GetService<StaticAssets>(), reactor.GetService<RingPool<AssetReader>>()));

    private readonly struct FileResponder(StaticAssets assets, RingPool<AssetReader> readers) : ITcpResponder
    {
        public async ValueTask RespondAsync(TcpConnection conn, RecvSnapshot snapshot)
        {
            // Hold the snapshot for the whole request so a concurrent reload can't free the fd or
            // baked response out from under an in-flight read/send.
            using StaticAssets.Lease lease = assets.Acquire();

            if (!RequestParser.TryFindAsset(conn, snapshot, lease, out AssetCache.Asset asset))
            {
                conn.Write(Responses.NotFound);
                await conn.FlushAsync();
                return;
            }

            // Revalidate against disk (size + mtime + inode). The baked response is the hot path
            // only while the file is unchanged; an edit or atomic rename is served live instead, so
            // RAM never goes stale. (Re-bakes on Reload().)
            bool fresh = AssetCache.IsFresh(asset, out bool exists, out long size);

            if (!exists)
            {
                conn.Write(Responses.NotFound);                                     // vanished
                await conn.FlushAsync();
            }
            else if (fresh && asset.Response != 0)
            {
                await Responses.SendChunkedAsync(conn, asset.Response, asset.ResponseLength);   // baked
            }
            else if (fresh)
            {
                await SendFromDiskAsync(conn, readers, asset, asset.Fd, asset.Length);          // large
            }
            else
            {
                await SendChangedFromDiskAsync(conn, readers, asset, size);                     // live
            }
        }
    }

    /// <summary>
    /// Stream an asset off the ring from <paramref name="fd"/>, framing Content-Length from
    /// <paramref name="totalLength"/>. Files bigger than the reader's buffer are read in successive
    /// chunks at advancing offsets, so they're served whole instead of truncated.
    /// </summary>
    private static async Task SendFromDiskAsync(
        TcpConnection conn,
        RingPool<AssetReader> readers,
        AssetCache.Asset asset,
        int fd,
        long totalLength)
    {
        AssetReader reader = await readers.RentAsync();
        try
        {
            int first = await reader.ReadAsync(fd, offset: 0);
            if (first < 0)
            {
                conn.Write(Responses.ServerError);
                await conn.FlushAsync();
                return;
            }

            Responses.WriteAssetHeader(conn, asset, (int)totalLength);   // full length up front
            await Responses.SendChunkedAsync(conn, reader.Buffer, first);

            long offset = first;
            while (offset < totalLength)
            {
                int read = await reader.ReadAsync(fd, offset);
                if (read <= 0)
                {
                    break;   // EOF or mid-stream error; the response is already committed
                }
                await Responses.SendChunkedAsync(conn, reader.Buffer, read);
                offset += read;
            }
        }
        finally
        {
            readers.Return(reader);
        }
    }

    /// <summary>
    /// Serve a file whose on-disk version no longer matches the baked snapshot: open the current
    /// path fresh (so an atomic rename resolves to the new inode, not the cached fd) and stream it
    /// live.
    /// </summary>
    private static async Task SendChangedFromDiskAsync(
        TcpConnection conn,
        RingPool<AssetReader> readers,
        AssetCache.Asset asset,
        long size)
    {
        SafeFileHandle handle;
        try
        {
            handle = System.IO.File.OpenHandle(asset.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch
        {
            conn.Write(Responses.NotFound);
            await conn.FlushAsync();
            return;
        }

        try
        {
            int fd = (int)handle.DangerousGetHandle();
            await SendFromDiskAsync(conn, readers, asset, fd, size);
        }
        finally
        {
            handle.Dispose();
        }
    }
}
