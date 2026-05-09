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
    private const uint   RingEntries = 4096;
    internal const int   BufferSize = 16 * 1024;

    // user_data layout: kind in high 32 bits, fd in low 32 bits.
    internal const ulong KindAccept = 1UL << 32;
    internal const ulong KindRecv   = 2UL << 32;
    internal const ulong KindSend   = 3UL << 32;

    // Pre-built HTTP/1.1 response shared across all reactors (read-only after init).
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
            
            threads[i] = new Thread(reactor.Run) { Name = $"reactor-{i}", IsBackground = false };
            threads[i].Start();
        }

        foreach (var t in threads)
        {
            t.Join();
        }
        
        return 0;
    }
}

/// <summary>
/// Per-connection handler. Lives outside Program's unsafe context so it can
/// use async/await. All pointer-touching work is delegated through Connection helpers.
/// </summary>
internal static class Handler
{
    public static async Task HandleAsync(Reactor reactor, int fd, Connection conn)
    {
        // Recv is multishot — armed once by Reactor.Dispatch on accept. The
        // handler just awaits results and sends responses.
        try
        {
            while (true)
            {
                int n = await conn.ReadAsync();
                if (n <= 0)
                {
                    conn.Close(fd);
                    return;
                }
                conn.QueueResponse(fd);
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
