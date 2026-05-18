using System.Runtime.InteropServices;
using static MinimaZero.Native;

namespace MinimaZero;

/// <summary>
/// Single-reactor HTTP/1.1 server using io_uring zero-copy receive (zcrx):
/// the NIC DMAs payload straight into a registered userspace area, so the
/// kernel never copies socket data into our buffers.
///
/// ┌─ HOST SETUP REQUIRED (no code analog) ───────────────────────────────┐
/// │ Kernel >= 6.15 with a zcrx-capable driver (bnxt/mlx5/...), or         │
/// │ netdevsim for the in-tree selftest. Then, for NIC "eth0" serving on   │
/// │ Port, steer the listener's flows onto ONE dedicated Rx queue:        │
/// │                                                                       │
/// │   ethtool -G eth0 tcp-data-split on              # header/data split  │
/// │   ethtool -L eth0 combined 2                     # >=2 queues         │
/// │   ethtool -X eth0 equal 1                        # RSS off the zc q   │
/// │   ethtool -N eth0 flow-type tcp4 dst-port 8080 action 1   # -> rxq 1  │
/// │                                                                       │
/// │ Set IfName / IfRxq below to match (here: "eth0", queue 1).            │
/// │ Without split + steering the recv_zc op errors or copy-falls-back.   │
/// └───────────────────────────────────────────────────────────────────────┘
///
/// One reactor only: a zcrx ifq binds to a single HW Rx queue, which does
/// not compose with multi-reactor SO_REUSEPORT load-balancing (that would
/// need one steered HW queue per ring). See Reactor.cs.
/// </summary>
internal static unsafe class Program
{
    private const ushort Port        = 8080;
    private const uint   RingEntries = 8192;
    internal const int   BufferSize  = 32 * 1024;

    // NIC + hardware Rx queue the zcrx ifq binds to. Must match the ethtool
    // flow-steering rule above.
    private const string IfName = "eth0";
    private const uint   IfRxq  = 1;

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

        uint ifIdx = if_nametoindex(IfName);
        if (ifIdx == 0)
        {
            Console.Error.WriteLine($"[MinimaZero] if_nametoindex(\"{IfName}\") failed; " +
                                    $"set IfName to a real interface.");
            return 1;
        }

        Console.WriteLine($"[MinimaZero] starting 1 zcrx reactor on port {Port} " +
                          $"(if={IfName} idx={ifIdx} rxq={IfRxq})");

        // Single reactor (see class docs for why zcrx is not multi-reactor here).
        var reactor = new Reactor(0, Port, RingEntries, ifIdx, IfRxq);
        var thread = new Thread(reactor.Run)
        {
            Name = "reactor-0",
            IsBackground = false
        };
        thread.Start();
        thread.Join();

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
                        // data is now usable with any BCL Memory<byte>/async API.
                        // It is a zero-copy view straight into NIC-DMA'd memory.
                        _ = data.Length;

                        // Recycle the chunk: token verbatim, length we consumed.
                        reactor.RefillRqe(mem.Off, (uint)mem.Length);
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
