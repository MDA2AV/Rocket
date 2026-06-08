using System.Buffers;
using System.IO.Pipelines;
using System.Text.Json;
using Minima.Utils;

namespace Minima;

/// <summary>
/// Multi-reactor HTTP/1.1 server using io_uring directly. Spawns N reactor
/// threads (one per CPU); each opens its own SO_REUSEPORT listener, runs its
/// own io_uring, owns its own connection map. The kernel load-balances new
/// connections across reactors. Per-connection state never crosses threads,
/// so no synchronization is needed on the hot path.
/// </summary>
internal static unsafe class Program
{
    internal static ReadOnlySpan<byte> Response =>
        "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 2\r\n\r\nok"u8;

    private static int Main()
    {
        // All tunables live in ServerConfig — override the defaults here.
        var config = new ServerConfig()
        {
            Port = 8080,
            UsePipe = false,
            ReactorCount = 2
        };

        Console.WriteLine($"[Minima] starting {config.ReactorCount} reactors on port {config.Port} (incremental={config.Incremental})");

        Handler.Init(config);

        var threads = new Thread[config.ReactorCount];
        for (var i = 0; i < config.ReactorCount; i++)
        {
            var reactor = new Reactor(i, config);

            threads[i] = new Thread(reactor.Run)
            {
                Name = $"reactor-{i}",
                IsBackground = false
            };
            threads[i].Start();
        }

        foreach (var t in threads)
        {
            t.Join();
        }

        return 0;
    }
}

internal static class Handler
{
    // Magpie io_uring file reader — opened once, shared across reactors. io_uring reads
    // use an explicit offset, so concurrent reads on the shared fd are safe.
    private static Magpie.MagpieReader _magpie = null!;
    private static int    _fd;
    private static int    _fileSize;
    private static byte[] _headers = null!;

    // Builds an HTML file of exactly `size` bytes (ASCII) for the file-serve demo.
    private static string BuildSample(int size)
    {
        const string head = "<!doctype html><html><head><title>Magpie</title></head><body>"
                           + "<h1>Served from disk via io_uring (Magpie)</h1><pre>";
        const string tail = "</pre></body></html>";
        return head + new string('x', size - head.Length - tail.Length) + tail;
    }

    public static void Init(ServerConfig config)
    {
        // Self-contained: drop a sample file if the configured path is missing, so the
        // demo runs anywhere. Point ServerConfig.FilePath at a real file to serve it.
        if (!File.Exists(config.FilePath))
        {
            File.WriteAllText(config.FilePath, BuildSample(10 * 1024));
        }

        _magpie   = new Magpie.MagpieReader(rings: config.ReactorCount);
        _fd       = Magpie.MagpieReader.OpenRead(config.FilePath);
        _fileSize = (int)Magpie.MagpieReader.Size(_fd);
        _headers  = System.Text.Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: {_fileSize}\r\n\r\n");

        Console.WriteLine($"[Minima] serving '{config.FilePath}' ({_fileSize} bytes) via Magpie");
    }

    // Real async-work knob: serialize an in-memory object of WORK_ITEMS elements to JSON
    // on the THREAD POOL (via Task.Run) per request. 0 / unset = disabled (pure inline
    // reactor path). Genuine CPU + allocation, not a busy-spin.
    private static readonly int WorkItems = 1;

    private static readonly Payload LargeObject = BuildPayload(Math.Max(WorkItems, 1));

    private static Payload BuildPayload(int count)
    {
        var items = new Item[count];
        for (int i = 0; i < count; i++)
        {
            items[i] = new Item(i, $"item-{i}", i * 1.5, (i & 1) == 0, $"category-{i % 8}");
        }
        return new Payload(DateTime.UtcNow.ToString("O"), count, items);
    }

    public static async Task HandleAsync(Reactor reactor, Connection conn)
    {
        try
        {
            while (true)
            {
                RecvSnapshot snap = await conn.ReadAsync();

                while (conn.TryGetItem(snap, out SpscRecvRing.Item item))
                {
                    if (item.HasBuffer)
                    {
                        UnmanagedMemoryManager mem = item.AsMemoryManager();
                        ReadOnlyMemory<byte> data = mem.Memory;
                        // data is now usable with any BCL Memory<byte>/async API
                        _ = data.Length;

                        // Cross-thread safe and mode-agnostic: routes to the
                        // shared-ring return or the incremental refcounted return.
                        conn.ReturnBuffer(in item);
                    }
                }

                // Serve the file: write the precomputed headers, then read the file body
                // straight into the connection's write slab via Magpie (io_uring). On a
                // warm page cache this completes inline on the reactor thread; the first
                // (cold) read blocks the reactor briefly while io-wq hits disk.
                //conn.Write(_headers);
                //Span<byte> body = conn.GetSpan(_fileSize);
                //int n = _magpie.Read(_fd, body, 0);
                //conn.Advance(n);
                
                //_ = await Task.Run(static () => JsonSerializer.Serialize("Hello World!"));
                
                conn.Write(Program.Response);
                await conn.FlushAsync();

                if (snap.IsClosed)
                {
                    // Reactor already owns teardown (Connections.Remove + close
                    // happens in Dispatch's recv-error branch); we just exit.
                    return;
                }

                conn.ResetRead();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[r{reactor.Id}] handler crash on fd={conn.ClientFd}: {ex}");
            // Reactor will clean the connection up via the recv-error path
            // (or SPSC overflow) on the next CQE for this fd.
        }
        finally
        {
            conn.DecRef();   // release the handler's ref; teardown runs once the reactor releases too
        }
    }

    // PipeReader/PipeWriter variant — same behavior, driven through the BCL
    // pipe adapters instead of the raw ReadAsync/TryGetItem/Write API.
    public static async Task HandlePipeAsync(Reactor reactor, Connection conn)
    {
        var reader = new ConnectionPipeReader(conn);
        var writer = new ConnectionPipeWriter(conn);

        try
        {
            while (true)
            {
                ReadResult read = await reader.ReadAsync();
                ReadOnlySequence<byte> buffer = read.Buffer;

                if (!buffer.IsEmpty)
                {
                    // A real server would parse requests out of `buffer` here.
                    writer.Write(Program.Response);
                    await writer.FlushAsync();
                }

                // Consume everything we got; AdvanceTo returns the recv buffers.
                reader.AdvanceTo(buffer.End);

                if (read.IsCompleted)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[r{reactor.Id}] pipe handler crash on fd={conn.ClientFd}: {ex}");
        }
        finally
        {
            reader.Complete();
            writer.Complete();
            conn.DecRef();
        }
    }
}

internal sealed record Item(int Id, string Name, double Value, bool Active, string Category);
internal sealed record Payload(string Generated, int Count, Item[] Items);
