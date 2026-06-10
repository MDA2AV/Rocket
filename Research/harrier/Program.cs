using System.Text.Json;
using Harrier;

// ReSharper disable SuggestVarOrType_BuiltInTypes

/// <summary>
/// harrier — an epoll server in Tokio's readiness model.
///
/// The reactor thread is a pure READINESS notifier: epoll_wait -> SignalReadable.
/// It NEVER touches the socket. The per-connection handler resumes on the thread
/// pool (RunContinuationsAsynchronously = true) and does its OWN recv()/send() on
/// its thread. So the recv buffer is owned by a single thread (no driver/handler
/// race), and the reactor pumps readiness for many connections without ever
/// blocking on I/O — exactly like Tokio/mio (reactor flips readiness + wakes the
/// task; the task does the read).
///
/// HARRIER_WORK=1 adds a per-request Task.Run (the async-work knob, like Shrike's
/// Playground) to exercise the coroutine/thread-pool path.
///
/// Env: HARRIER_PORT (8080), HARRIER_WORKERS (ProcessorCount/2), HARRIER_WORK (0).
/// </summary>
internal static class Program
{
    private static readonly bool DoWork = Environment.GetEnvironmentVariable("HARRIER_WORK") == "1";

    private static void Main()
    {
        int port = 8080;
        if (int.TryParse(Environment.GetEnvironmentVariable("HARRIER_PORT"), out int p) && p > 0) port = p;

        int workers = Math.Max(1, Environment.ProcessorCount / 2);
        if (int.TryParse(Environment.GetEnvironmentVariable("HARRIER_WORKERS"), out int w) && w > 0) workers = w;

        // Pre-warm the pool so the async handlers don't wait on thread-pool ramp.
        ThreadPool.SetMinThreads(Environment.ProcessorCount, Environment.ProcessorCount);

        Console.WriteLine($"harrier: {workers} reactors, work={DoWork}");
        HarrierEngine.CreateBuilder()
            .SetPort(port)
            .SetBacklog(16384)
            .SetMaxEventsPerWake(512)
            .SetMaxNumberConnectionsPerWorker(8192)
            .SetSlabSizes(32 * 1024, 32 * 1024)
            .SetNWorkersSolver(() => workers)
            .InjectHandler(HandleAsync)
            .Build()
            .Run();
    }

    /// <summary>
    /// Tokio-style per-connection handler. RCA=true → resumes on the thread pool.
    /// ReadAsync waits for readability; the handler then recv's itself (DoRecv),
    /// parses, and sends. Only this thread touches the connection buffers.
    /// </summary>
    private static async Task HandleAsync(Connection conn)
    {
        while (true)
        {
            if (await conn.ReadAsync())        // wait for readability (true => peer closed)
                return;

            if (!conn.DoRecv())                // recv on THIS (handler) thread
                return;                        // peer closed / hard error

            bool wrote = DrainPlain(conn);     // one response per complete request

            if (wrote)
            {
                if (DoWork)
                    _ = await Task.Run(static () => JsonSerializer.Serialize("Hello World!"));
                await conn.FlushAsync();
            }
        }
    }

    /// <summary>Fast plaintext drain: one response per complete request (delimited by
    /// CRLFCRLF — GET, no body), skipping route/header parsing.</summary>
    private static unsafe bool DrainPlain(Connection conn)
    {
        bool wrote = false;
        int consumed = 0;
        ReadOnlySpan<byte> buf = new(conn.ReceiveBuffer + conn.Head, conn.Tail - conn.Head);
        while (true)
        {
            int idx = buf[consumed..].IndexOf("\r\n\r\n"u8);
            if (idx < 0) break;
            consumed += idx + 4;
            CommitPlainText(conn);
            wrote = true;
        }
        conn.Head += consumed;
        conn.Compact();
        return wrote;
    }

    private static unsafe void CommitPlainText(Connection conn) =>
        conn.WriteBuffer.WriteUnmanaged(
            "HTTP/1.1 200 OK\r\nServer: H\r\nContent-Type: text/plain\r\nContent-Length: 13\r\n\r\nHello, World!"u8);
}
