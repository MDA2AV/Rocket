using System.Runtime.InteropServices;

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

    // user_data layout: kind in high 32 bits, fd in low 32 bits.
    internal const ulong KindAccept = 1UL << 32;
    internal const ulong KindRecv   = 2UL << 32;
    internal const ulong KindSend   = 3UL << 32;

    // Pre-built HTTP/1.1 response shared across all reactors
    internal static byte* s_responseBytes;
    internal static int   s_responseLen;

    private static ReadOnlySpan<byte> s_response => "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 2\r\n\r\nok"u8;

    private static int Main()
    {
        s_responseLen = s_response.Length;
        s_responseBytes = (byte*)NativeMemory.Alloc((nuint)s_responseLen);
        s_response.CopyTo(new Span<byte>(s_responseBytes, s_responseLen));

        var n = 12;
        Console.WriteLine($"[Minima] starting {n} reactors on port {Port}");

        var threads = new Thread[n];
        for (var i = 0; i < n; i++)
        {
            var reactor = new Reactor(i, Port, RingEntries);
            
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
    public static async Task HandleAsync(Reactor reactor, int fd, Connection conn)
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

                        reactor.ReturnBuffer(mem.BufferId);
                    }
                    conn.QueueResponse(fd);
                }

                if (snap.IsClosed)
                {
                    conn.Close(fd);
                    return;
                }

                conn.ResetRead();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[r{reactor.Id}] handler crash on fd={fd}: {ex}");
            conn.Close(fd);
        }
    }
}
