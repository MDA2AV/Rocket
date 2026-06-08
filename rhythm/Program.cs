namespace Rhythm;

/// <summary>
/// rhythm — a synchronous, single-issuer io_uring HTTP/1.1 server.
///
/// Spawns RHYTHM_REACTORS reactor threads (default 12), each with its own ring +
/// SO_REUSEPORT listener (shared-nothing). Unpinned by default; set RHYTHM_PIN=1
/// to pin reactor i to the i-th allowed CPU. The dataset is loaded once and
/// shared read-only across reactors.
///
/// Env: RHYTHM_PORT (8080), RHYTHM_REACTORS (12), RHYTHM_PIN (off), RHYTHM_DATASET
///      (/data/dataset.json).
/// </summary>
internal static class Program
{
    private static int Main()
    {
        ushort port = 8080;
        if (ushort.TryParse(Environment.GetEnvironmentVariable("RHYTHM_PORT"), out ushort p) && p > 0)
            port = p;

        var dsPath = Environment.GetEnvironmentVariable("RHYTHM_DATASET") ?? "/data/dataset.json";
        var ds = Dataset.Load(dsPath);

        int reactors = 12;
        if (int.TryParse(Environment.GetEnvironmentVariable("RHYTHM_REACTORS"), out int r) && r > 0)
            reactors = r;

        // Pinning is opt-in (RHYTHM_PIN=1), off by default. When enabled, reactor
        // i is pinned to the i-th allowed CPU (round-robin); otherwise unpinned.
        var pinEnv = Environment.GetEnvironmentVariable("RHYTHM_PIN");
        bool pin = pinEnv == "1" || string.Equals(pinEnv, "true", StringComparison.OrdinalIgnoreCase);
        Span<int> cpus = stackalloc int[256];
        int ncpu = pin ? Affinity.Allowed(cpus) : 0;

        Console.WriteLine($"[rhythm] {reactors} synchronous reactors on :{port} " +
                          $"(pinned={pin}, {ds.Count} dataset items)");

        var threads = new Thread[reactors];
        for (int i = 0; i < reactors; i++)
        {
            int cpu = pin && ncpu > 0 ? cpus[i % ncpu] : -1; // -1 = unpinned (Affinity.Pin no-ops)
            var reactor = new Reactor(i, port, cpu, ds);
            threads[i] = new Thread(reactor.Run) { Name = $"reactor-{i}", IsBackground = false };
            threads[i].Start();
        }
        foreach (var t in threads) t.Join();
        return 0;
    }
}
