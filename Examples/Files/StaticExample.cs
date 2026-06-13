using ioxide;
using ioxide.file;

namespace Examples.Files;

/// <summary>
/// Static files over the per-reactor asset cache. A <see cref="StaticAssets.Lease"/> keeps the
/// snapshot alive for the whole request (so a concurrent reload can't free the fd mid-send). Small
/// files are served from a baked HTTP response with no I/O; larger files are read off the ring in
/// chunks. Misses are 404.
/// </summary>
public static class StaticExample
{
    private const int Chunk = 12 * 1024;

    public static async Task Handle(Reactor r, Connection conn)
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
                        Http.WriteText(conn, 404, "Not Found", $"no asset {path}");
                        await conn.FlushAsync();
                    }
                    else if (asset.Response != 0)
                    {
                        await SendNative(conn, asset.Response, asset.ResponseLength);
                    }
                    else
                    {
                        await SendFromDisk(conn, readers, asset);
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

    // Read a large (non-baked) asset off the ring, in successive chunks for files bigger than the buffer.
    private static async Task SendFromDisk(Connection conn, RingPool<AssetReader> readers, AssetCache.Asset asset)
    {
        AssetReader reader = await readers.RentAsync();

        try
        {
            int first = await reader.ReadAsync(asset.Fd, offset: 0);
            if (first < 0)
            {
                Http.WriteText(conn, 500, "Internal Server Error", "read failed");
                await conn.FlushAsync();
                return;
            }

            WriteHeader(conn, asset);
            await SendNative(conn, reader.Buffer, first);

            long offset = first;
            while (offset < asset.Length)
            {
                int read = await reader.ReadAsync(asset.Fd, offset);
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

    // Format the 200 header into a stack buffer (kept in a sync method - a Span can't cross an await).
    private static void WriteHeader(Connection conn, AssetCache.Asset asset)
    {
        Span<byte> header = stackalloc byte[256];
        conn.Write(header[..AssetCache.WriteResponseHeader(header, asset.Path, (int)asset.Length)]);
    }

    // Copy native memory through the write slab in slab-sized chunks and flush each.
    private static async Task SendNative(Connection conn, nint data, int length)
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

    private static unsafe void WriteChunk(Connection conn, nint chunk, int length)
    {
        conn.Write(new ReadOnlySpan<byte>((void*)chunk, length));
    }
}
