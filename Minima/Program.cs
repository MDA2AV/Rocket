using Minima.Connection;
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
    private const ushort Port       = 8080;
    private const uint   RingEntries = 8192;
    internal const int   BufferSize = 32 * 1024;

    // Per-reactor toggle: false = one shared buf_ring (simple path); true =
    // per-connection rings with incremental consumption (IOU_PBUF_RING_INC).
    private const bool   Incremental = true;

    // user_data layout: kind in high 32 bits, fd in low 32 bits.
    internal const ulong KindAccept = 1UL << 32;
    internal const ulong KindRecv   = 2UL << 32;
    internal const ulong KindSend   = 3UL << 32;
    internal const ulong KindWake   = 4UL << 32;  // eventfd-based cross-thread wake

    internal static ReadOnlySpan<byte> Response =>
        "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 2\r\n\r\nok"u8;

    private static int Main()
    {
        var n = 12;
        Console.WriteLine($"[Minima] starting {n} reactors on port {Port}");

        var threads = new Thread[n];
        for (var i = 0; i < n; i++)
        {
            var reactor = new Reactor(i, Port, RingEntries, Incremental);
            
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
    public static async Task HandleAsync(Reactor reactor, Connection.Connection conn)
    {
        try
        {
            while (true)
            {
                RecvSnapshot snap = await conn.ReadAsync();

                await Task.Delay(0);

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
}
