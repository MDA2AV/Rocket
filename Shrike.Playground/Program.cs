// ReSharper disable always SuggestVarOrType_BuiltInTypes
using System.Runtime.CompilerServices;
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
                await conn.FlushAsync();
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
