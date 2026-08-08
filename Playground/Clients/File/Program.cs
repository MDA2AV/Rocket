using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using ioxide;
using ioxide.file;
using ioxide.utils;
using Playground.Shared;

// ─────────────────────────────────────────────────────────────────────────────────────────────
//  file - a static file server, whole. The asset cache opens every file ONCE and shares the
//  descriptors across all reactors: they are stable and read positionally, so nothing is locked.
//  Small files are served from a pre-baked HTTP response with no I/O at all; larger ones stream
//  off the ring through a rented reader.
//
//  Every hit is revalidated against disk (size + mtime + inode), so an edit or an atomic rename is
//  served live from the new inode rather than stale from RAM.
//
//      PLAYGROUND_DIR=/srv/www dotnet run -c Release --project Playground/File
//      curl http://127.0.0.1:8080/index.html
//      kill -HUP <pid>        # reload the snapshot after a deploy
//
//  Needs: ioxide, ioxide.file
// ─────────────────────────────────────────────────────────────────────────────────────────────

string dir = Env.Str("PLAYGROUND_DIR", "/tmp/ioxide-assets");

// Per-file byte ceiling for pinning bodies in memory. 0 forces every request through the ring-read
// path, which is the interesting one to benchmark.
int cacheMax = Env.Int("PLAYGROUND_CACHE_MAX", AssetCache.DefaultMaxCachedFileBytes);

SampleAssets.Ensure(dir);   // writes a demo index.html + style.css if the directory is empty

// Built once for the whole process, BEFORE the reactors start: every reactor shares this snapshot.
var assets = new StaticAssets(
    dir,        // served root (PLAYGROUND_DIR); walked once into an immutable snapshot
    cacheMax);  // maxCachedFileBytes: whole-response pin ceiling per file (0 = always ring-read)

var config = new ServerConfig
{
    ReactorCount   = Env.Int("PLAYGROUND_REACTORS", Environment.ProcessorCount),  // io_uring rings/threads - one per core
    RingEntries    = 8192,                                                        // SQ/CQ depth per ring
    DualStack      = false,                                                       // true = one IPv6 socket also accepts IPv4-mapped
    RecvBufferSize = 32 * 1024,                                                   // bytes per shared recv buffer
    RecvSlots      = 4096,                                                        // shared recv buffer-ring depth
    Incremental    = null,                                                        // per-connection recv rings (6.12+) - see Tcp/Incremental
    Udp            = null,                                                        // no raw UDP sockets (TCP-only server)
    Quic           = null,                                                        // no QUIC transport - see Http3/* and Quic/Alpn
    Tcp = new TcpOptions
    {
        Port             = Env.Port("PLAYGROUND_PORT", 8080),
        ExtraPorts       = [],                                                    // extra listener ports (one handler, several doors)
        ListenBacklog    = 1024,                                                  // accept-queue depth per SO_REUSEPORT listener
        WriteSlabSize    = 16 * 1024,                                             // per-connection write buffer before overflow kicks in
        PoolMax          = 1024,                                                  // pooled connection objects kept per reactor
        WriteOverflow    = WriteOverflowStrategy.Grow,                            // Grow = realloc one slab; Segmented = chain + vectored SENDMSG
        ZeroCopySend     = false,                                                 // SEND_ZC: kernel copies less, wins on large writes
        RecvQueueEntries = 64,                                                    // per-connection recv completion queue depth
    },
};

const int BodyChunk = 12 * 1024;   // how much native memory we push through the write slab at a time

var threads = new Thread[config.ReactorCount];

for (int i = 0; i < threads.Length; i++)
{
    var reactor = new Reactor(i, config);

    reactor.OnStart = r =>
    {
        r.AddService(assets);   // the shared snapshot
        AssetReader.CreatePool(r,
            readers:     4,         // concurrent ring reads bounded per reactor (pool size)
            bufferBytes: 1 << 20);  // native read buffer per reader (1 MiB) - size for the largest asset
    };

    reactor.TcpHandle = async (r, conn) =>
    {
        StaticAssets snapshot = r.GetService<StaticAssets>();
        RingPool<AssetReader> readers = r.GetService<RingPool<AssetReader>>();

        try
        {
            while (true)
            {
                RecvSnapshot recv = await conn.ReadAsync();

                // The lease pins the snapshot for the whole request, so a concurrent Reload() can't
                // free the fd or the baked response out from under an in-flight send.
                using (StaticAssets.Lease lease = snapshot.Acquire())
                {
                    // Resolve the target against the cache while the recv bytes are still valid -
                    // the lookup is span-based, so no string is ever allocated for a cache hit.
                    bool found = false;
                    AssetCache.Asset asset = default;

                    while (conn.TryGetItem(recv, out SpscRecvRing.Item item))
                    {
                        if (item.HasBuffer)
                        {
                            if (!found && TryReadTarget(item.AsSpan(), out ReadOnlySpan<byte> target))
                            {
                                found = lease.TryGet(target, out asset);
                            }
                            conn.ReturnBuffer(in item);
                        }
                    }

                    if (!found)
                    {
                        conn.Write("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\n\r\n"u8);
                        await conn.FlushAsync();
                    }
                    else
                    {
                        // Is what we cached still what's on disk?
                        bool fresh = AssetCache.IsFresh(asset, out bool exists, out long size);

                        if (!exists)
                        {
                            conn.Write("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\n\r\n"u8);
                            await conn.FlushAsync();
                        }
                        else if (fresh && asset.Response != 0)
                        {
                            // The hot path: a complete HTTP response baked at snapshot time, sitting
                            // in native memory. No open, no read, no formatting - just send it.
                            await SendNativeAsync(conn, asset.Response, asset.ResponseLength);
                        }
                        else if (fresh)
                        {
                            // Too big to pin: read it off the ring at advancing offsets.
                            await SendFromDiskAsync(conn, readers, asset, asset.Fd, asset.Length);
                        }
                        else
                        {
                            // Changed under us: open the path fresh so an atomic rename resolves to
                            // the NEW inode, and stream that instead of serving stale bytes.
                            await SendChangedAsync(conn, readers, asset, size);
                        }
                    }
                }

                if (recv.IsClosed) return;
                conn.ResetRead();
            }
        }
        finally
        {
            conn.DecRef();
        }
    };

    threads[i] = new Thread(reactor.Run) { Name = $"reactor-{i}" };
    threads[i].Start();
}

// Reload on SIGHUP (kill -HUP <pid>): a fresh snapshot is opened and swapped in atomically, and the
// old descriptors close after a grace period - so in-flight requests finish on the bytes they
// started with.
using var reload = PosixSignalRegistration.Create(PosixSignal.SIGHUP, context =>
{
    context.Cancel = true;   // handle it; don't let the default action terminate us
    assets.Reload();
    Console.WriteLine($"[file] reloaded - now serving {assets.Count} files");
});

Console.WriteLine($"[file] {config.ReactorCount} reactors on :{config.Tcp.Port} - "
                + $"{assets.Count} files under {assets.RootDir} (pin <= {cacheMax}B)");

foreach (Thread thread in threads)
{
    thread.Join();
}

// Push native memory through the write slab in chunks: one flush for a small payload, a short
// sequence for anything bigger than the slab.
static async Task SendNativeAsync(TcpConnection conn, nint data, int length)
{
    int sent = 0;
    while (true)
    {
        int chunk = Math.Min(length - sent, BodyChunk);
        unsafe
        {
            conn.Write(new ReadOnlySpan<byte>((void*)(data + sent), chunk));
        }
        await conn.FlushAsync();
        sent += chunk;

        if (sent >= length) return;
    }
}

// Stream an asset off the ring, framing Content-Length up front. Files bigger than the reader's
// buffer are read in successive chunks at advancing offsets, so they're served whole.
static async Task SendFromDiskAsync(
    TcpConnection conn, RingPool<AssetReader> readers, AssetCache.Asset asset, int fd, long totalLength)
{
    AssetReader reader = await readers.RentAsync();
    try
    {
        int first = await reader.ReadAsync(fd, offset: 0);   // io_uring positional read
        if (first < 0)
        {
            conn.Write("HTTP/1.1 500 Internal Server Error\r\nContent-Length: 0\r\n\r\n"u8);
            await conn.FlushAsync();
            return;
        }

        Span<byte> header = stackalloc byte[256];
        conn.Write(header[..AssetCache.WriteResponseHeader(header, asset.Path, (int)totalLength)]);
        await SendNativeAsync(conn, reader.Buffer, first);

        long offset = first;
        while (offset < totalLength)
        {
            int read = await reader.ReadAsync(fd, offset);
            if (read <= 0) break;   // EOF or mid-stream error; the response is already committed
            await SendNativeAsync(conn, reader.Buffer, read);
            offset += read;
        }
    }
    finally
    {
        readers.Return(reader);
    }
}

static async Task SendChangedAsync(
    TcpConnection conn, RingPool<AssetReader> readers, AssetCache.Asset asset, long size)
{
    SafeFileHandle handle;
    try
    {
        handle = File.OpenHandle(asset.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }
    catch
    {
        conn.Write("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\n\r\n"u8);
        await conn.FlushAsync();
        return;
    }

    try
    {
        await SendFromDiskAsync(conn, readers, asset, (int)handle.DangerousGetHandle(), size);
    }
    finally
    {
        handle.Dispose();
    }
}

static bool TryReadTarget(ReadOnlySpan<byte> request, out ReadOnlySpan<byte> target)
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
