using System.Buffers.Text;
using System.Runtime.InteropServices;
using ioxide;
using ioxide.file;
using ioxide.utils;
using Playground.Shared;

// ─────────────────────────────────────────────────────────────────────────────────────────────
//  file - a static file server. ioxide.file opens every file under the root ONCE and shares the
//  descriptors across all reactors - stable and read positionally off the ring, so nothing is
//  locked and nothing is cached in memory. This sample frames HTTP/1.1 around the bytes; the same
//  file data can be handed to any protocol.
//
//  Descriptors are trusted for the snapshot's lifetime - a deploy is picked up by reloading the
//  snapshot (SIGHUP), which reopens the files, not by re-stat'ing every request.
//
//      PLAYGROUND_DIR=/srv/www dotnet run -c Release --project Playground/Clients/File
//      curl http://127.0.0.1:8080/index.html
//      kill -HUP <pid>        # reload the snapshot after a deploy
//
//  Needs: ioxide, ioxide.file
// ─────────────────────────────────────────────────────────────────────────────────────────────

string dir = Env.Str("PLAYGROUND_DIR", "/tmp/ioxide-assets");

SampleAssets.Ensure(dir);   // writes a demo index.html + style.css if the directory is empty

// Built once for the whole process, BEFORE the reactors start: every reactor shares this snapshot.
var assets = new StaticAssets(dir);   // served root (PLAYGROUND_DIR), walked once into a snapshot

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
                // close the fd out from under an in-flight read.
                using (StaticAssets.Lease lease = snapshot.Acquire())
                {
                    // Resolve the target against the snapshot while the recv bytes are still valid -
                    // the lookup is span-based, so no string is ever allocated for a hit.
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

                    // The fd is trusted for the snapshot's lifetime, so serving is just: read the
                    // bytes off the ring and frame HTTP around them.
                    if (found)
                    {
                        await SendFileAsync(conn, readers, asset.Path, asset.Fd, asset.Length);
                    }
                    else
                    {
                        conn.Write("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\n\r\n"u8);
                        await conn.FlushAsync();
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
                + $"{assets.Count} files under {assets.RootDir} (ring reads, nothing cached)");

foreach (Thread thread in threads)
{
    thread.Join();
}

// Stream a file off the ring, framing Content-Length up front. Each ring read (up to the reader's
// buffer) is one write + one flush; files bigger than the buffer take several reads at advancing
// offsets, so they're served whole with one flush per read rather than one per slab.
static async Task SendFileAsync(
    TcpConnection conn, RingPool<AssetReader> readers, string path, int fd, long totalLength)
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

        // ioxide.file hands over bytes; the HTTP framing is ours. Header + first chunk go out
        // together in one flush.
        Span<byte> header = stackalloc byte[256];
        conn.Write(header[..WriteHeader(header, path, totalLength)]);
        WriteNative(conn, reader.Buffer, first);
        await conn.FlushAsync();

        long offset = first;
        while (offset < totalLength)
        {
            int read = await reader.ReadAsync(fd, offset);
            if (read <= 0) break;   // EOF or mid-stream error; the response is already committed
            WriteNative(conn, reader.Buffer, read);
            await conn.FlushAsync();
            offset += read;
        }
    }
    finally
    {
        readers.Return(reader);
    }
}

// Copy native memory (a ring-read buffer) into the connection's write slab in one go.
static unsafe void WriteNative(TcpConnection conn, nint data, int length)
    => conn.Write(new ReadOnlySpan<byte>((void*)data, length));

// Write the 200 status line + Content-Type + Content-Length for this file.
static int WriteHeader(Span<byte> destination, string path, long bodyLength)
{
    int h = 0;
    h += Copy(destination[h..], "HTTP/1.1 200 OK\r\nContent-Type: "u8);
    h += Copy(destination[h..], MimeFor(path));
    h += Copy(destination[h..], "\r\nContent-Length: "u8);
    Utf8Formatter.TryFormat(bodyLength, destination[h..], out int digits);
    h += digits;
    h += Copy(destination[h..], "\r\n\r\n"u8);
    return h;
}

static int Copy(Span<byte> destination, ReadOnlySpan<byte> source)
{
    source.CopyTo(destination);
    return source.Length;
}

static ReadOnlySpan<byte> MimeFor(string path) => Path.GetExtension(path) switch
{
    ".html"  => "text/html"u8,
    ".css"   => "text/css"u8,
    ".js"    => "application/javascript"u8,
    ".json"  => "application/json"u8,
    ".svg"   => "image/svg+xml"u8,
    ".png"   => "image/png"u8,
    ".webp"  => "image/webp"u8,
    ".woff2" => "font/woff2"u8,
    ".txt"   => "text/plain"u8,
    _        => "application/octet-stream"u8
};

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
