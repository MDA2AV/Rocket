// ReSharper disable always SuggestVarOrType_BuiltInTypes
using System.Runtime.CompilerServices;
using System.Text.Json;
using Shrike;

[SkipLocalsInit]
internal static class Program
{
    public static void Main()
    {
        var engine = ShrikeEngine
            .CreateBuilder()
            .SetNWorkersSolver(() => 12)
            .SetBacklog(16384)
            .SetMaxEventsPerWake(512)
            .SetMaxNumberConnectionsPerWorker(512)
            .SetPort(8080)
            .SetSlabSizes(512 * 1024, 128 * 1024)
            .InjectHandler(HandleAsync);

        engine.Build().Run();
    }

    // Same knob + object as Minima / AspBaseline / SocketBaseline: serialize a
    // WORK_ITEMS-element object to JSON on the THREAD POOL per request. 0/unset = inline.
    private static readonly int WorkItems =
        int.TryParse(Environment.GetEnvironmentVariable("WORK_ITEMS"), out int n) ? n : 0;

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

    /// <summary>
    /// The per-connection handler — Minima-style. The handler owns the request
    /// lifecycle through the connection's IVTS-backed read/flush:
    ///   await ReadAsync  → wait for data (suspends until the worker recv's)
    ///   TryReadRequest   → parse each complete request from the recv window
    ///   write response   → into the connection's WriteBuffer
    ///   await FlushAsync → send (suspends if the socket back-pressures, EPOLLOUT)
    /// Runs inline on the worker thread, so it's a single-threaded cooperative loop.
    /// </summary>
    private static async Task HandleAsync(Connection conn)
    {
        while (true)
        {
            if (await conn.ReadAsync())          // true => peer closed
                return;

            bool wrote = false;
            while (conn.TryReadRequest())        // one iteration per complete request
            {
                CommitPlainTextResponse(conn);
                conn.Clear();
                wrote = true;
            }

            if (wrote)
            {
                // Real async work on the THREAD POOL — handler resumes off-worker. Shrike's
                // FlushAsync does a thread-safe send() directly (epoll), so no handoff here.
                if (WorkItems > 0)
                {
                    _ = await Task.Run(static () => JsonSerializer.SerializeToUtf8Bytes(LargeObject));
                }

                await conn.FlushAsync();
            }
        }
    }

    private static ReadOnlySpan<byte> s_plainTextBody => "Hello, World!"u8;

    private static unsafe void CommitPlainTextResponse(Connection connection)
    {
        int tail = connection.WriteBuffer.Tail;
        int contentLength = s_plainTextBody.Length;

        connection.WriteBuffer.WriteUnmanaged("HTTP/1.1 200 OK\r\n"u8 +
                                              "Content-Length:   \r\n"u8 +
                                              "Server: S\r\n"u8 +
                                              "Content-Type: text/plain\r\n"u8);
        connection.WriteBuffer.WriteUnmanaged(DateHelper.HeaderBytes);
        connection.WriteBuffer.WriteUnmanaged(s_plainTextBody);

        // Patch the 2-digit Content-Length into the reserved spaces (offset matches the header above).
        byte* dst = connection.WriteBuffer.Ptr + tail + 33;
        int tens = contentLength / 10;
        int ones = contentLength - tens * 10;
        dst[0] = (byte)('0' + tens);
        dst[1] = (byte)('0' + ones);
    }
}

internal sealed record Item(int Id, string Name, double Value, bool Active, string Category);
internal sealed record Payload(string Generated, int Count, Item[] Items);
