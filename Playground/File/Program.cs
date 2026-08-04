using Microsoft.Win32.SafeHandles;
using ioxide;
using ioxide.file;
using ioxide.utils;
using Playground.Shared;
using Playground.Shared.Http;
using Playground.Shared.Setup;

// file - static files over the shared asset cache: small assets are served from the snapshot's baked
// HTTP response with no I/O at all, larger ones are read off the ring through a rented reader, and
// misses are 404. SIGHUP swaps in a fresh snapshot atomically.
//
//   PLAYGROUND_DIR=/srv/www dotnet run -c Release --project Playground/File
//   kill -HUP <pid>     # reload after a deploy

string dir = Env.Str("PLAYGROUND_DIR", "/tmp/ioxide-assets");

// Per-file byte ceiling for pinning bodies in memory (0 forces every request through the ring-read
// path).
int cacheMax = Env.Int("PLAYGROUND_CACHE_MAX", AssetCache.DefaultMaxCachedFileBytes);

SampleAssets.Ensure(dir);
var assets = new StaticAssets(dir, cacheMax);

return PlaygroundHost.Run(new PlaygroundSample
{
    Name = "file",
    Summary = $"{assets.Count} files under {assets.RootDir} (pin <= {cacheMax}B)",
    Start = reactor =>
    {
        reactor.AddService(assets);
        AssetReader.CreatePool(reactor, readers: 4, bufferBytes: 1 << 20);
    },
    Tcp = (reactor, conn) => ConnectionLoop.ServeAsync(
        conn,
        new FileResponder(reactor.GetService<StaticAssets>(), reactor.GetService<RingPool<AssetReader>>())),
    OnReload = () =>
    {
        assets.Reload();
        Console.WriteLine($"[playground] reloaded - now serving {assets.Count} files");
    },
});

internal readonly struct FileResponder(StaticAssets assets, RingPool<AssetReader> readers) : ITcpResponder
{
    public async ValueTask RespondAsync(TcpConnection conn, RecvSnapshot snapshot)
    {
        // Hold the snapshot for the whole request so a concurrent reload can't free the fd or baked
        // response out from under an in-flight read/send.
        using StaticAssets.Lease lease = assets.Acquire();

        if (!TryFindAsset(conn, snapshot, lease, out AssetCache.Asset asset))
        {
            conn.Write(Responses.NotFound);
            await conn.FlushAsync();
            return;
        }

        // Revalidate against disk (size + mtime + inode). The baked response is the hot path only
        // while the file is unchanged; an edit or atomic rename is served live instead, so RAM never
        // goes stale. (Re-bakes on Reload().)
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

    /// <summary>
    /// Drain the recv, resolving the request target against the asset cache while the bytes are
    /// still valid. The lookup is span-based - no string - so it has to happen before the buffer
    /// goes back to the ring, which is why it lives here rather than in the shared parser.
    /// </summary>
    private static bool TryFindAsset(
        TcpConnection conn,
        RecvSnapshot snapshot,
        StaticAssets.Lease lease,
        out AssetCache.Asset asset)
    {
        bool found = false;
        asset = default;

        while (conn.TryGetItem(snapshot, out SpscRecvRing.Item item))
        {
            if (item.HasBuffer)
            {
                if (!found && RequestParser.TryReadTarget(item.AsSpan(), out ReadOnlySpan<byte> target))
                {
                    found = lease.TryGet(target, out asset);
                }

                conn.ReturnBuffer(in item);
            }
        }

        return found;
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

            WriteAssetHeader(conn, asset, (int)totalLength);   // full length up front
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

    private static void WriteAssetHeader(TcpConnection conn, AssetCache.Asset asset, int bodyLength)
    {
        Span<byte> header = stackalloc byte[256];
        conn.Write(header[..AssetCache.WriteResponseHeader(header, asset.Path, bodyLength)]);
    }
}
