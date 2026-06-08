namespace Loom;

/// <summary>
/// loom — an io_uring, thread-per-core server where each reactor installs a
/// <see cref="LoomSyncContext"/>, so handlers run inline on the reactor (fast) yet can
/// `await` arbitrary async work whose continuation is woven back onto the reactor thread.
///
/// Env: LOOM_PORT (8080), LOOM_REACTORS (one per CPU).
/// </summary>
internal static class Program
{
    private static int Main()
    {
        ushort port = 8080;
        if (ushort.TryParse(Environment.GetEnvironmentVariable("LOOM_PORT"), out ushort p) && p > 0) port = p;

        // Pre-warm the pool so off-reactor awaits aren't waiting on thread-pool ramp.
        ThreadPool.SetMinThreads(Environment.ProcessorCount, Environment.ProcessorCount);

        int reactors = 12;
        Http.MinimaMode = false;
        Http.Work = true;

        // env overrides for benching the modes: LOOM_MINIMA, LOOM_WORK (Task.Run), LOOM_RING (io_uring NOP)
        if (Environment.GetEnvironmentVariable("LOOM_MINIMA") is { } m) Http.MinimaMode = m == "1";
        if (Environment.GetEnvironmentVariable("LOOM_WORK") is { } w) Http.Work = w == "1";
        Http.RingWork = Environment.GetEnvironmentVariable("LOOM_RING") == "1";
        if (long.TryParse(Environment.GetEnvironmentVariable("LOOM_DELAY_US"), out long du)) Http.DelayUs = du;
        Http.UseDb = Environment.GetEnvironmentVariable("LOOM_DB") == "1";

        Console.WriteLine($"loom: {reactors} io_uring reactors on :{port} " +
                          $"(SyncContext per reactor, minima-mode={Http.MinimaMode})");

        var threads = new Thread[reactors];
        for (int i = 0; i < reactors; i++)
        {
            var reactor = new Reactor(i, port);
            threads[i] = new Thread(reactor.Run) { Name = $"reactor-{i}", IsBackground = false };
            threads[i].Start();
        }
        foreach (var t in threads) t.Join();
        return 0;
    }
}
