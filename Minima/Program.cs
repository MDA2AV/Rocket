using System.Buffers;
using System.IO.Pipelines;
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
            UsePipe = false,
        };

        Console.WriteLine($"[Minima] starting {config.ReactorCount} reactors on port {config.Port} (incremental={config.Incremental})");

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

                // One response per recv burst — accumulate in the connection's
                // per-connection write slab, then submit and await ack.
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
        }
    }
}
