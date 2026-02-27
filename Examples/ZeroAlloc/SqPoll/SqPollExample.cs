using zerg;
using zerg.Engine;
using zerg.Engine.Configs;
using zerg.Utils.UnmanagedMemoryManager;

namespace Examples.ZeroAlloc.SqPoll;

/// <summary>
/// Demonstrates running zerg with SQPOLL-enabled io_uring rings.
///
/// With SQPOLL the kernel spawns a dedicated polling thread per ring that
/// continuously drains the submission queue, eliminating the submit syscall
/// on the hot path. This trades a CPU core per reactor for lower latency
/// under sustained load.
///
/// The connection handler is the same zero-copy Rings_as_ReadOnlySpan
/// approach — the only difference is in the engine/reactor configuration.
///
/// NOTE: SQPOLL is incompatible with SINGLE_ISSUER and DEFER_TASKRUN:
///
///   - DEFER_TASKRUN defers kernel task work until userspace calls io_uring_enter().
///     SQPOLL's kernel thread drives submissions autonomously without io_uring_enter()
///     on the hot path, so there's no point to defer work to. The kernel rejects the
///     combination with -EINVAL.
///
///   - SINGLE_ISSUER tells the kernel only one thread touches the SQ, letting it skip
///     locking. With SQPOLL the kernel polling thread also reads the SQ concurrently,
///     making two issuers. The kernel rejects this too.
///
/// In practice, SQPOLL often performs *worse* for TCP servers because:
///   1. The default config (SINGLE_ISSUER | DEFER_TASKRUN + submit_and_wait_timeout)
///      already batches submit+wait into a single syscall — SQPOLL saves almost nothing.
///   2. Each reactor's SQPOLL kernel thread pins and spins on a dedicated core, starving
///      the .NET thread pool and handler tasks of CPU time.
///   3. Without DEFER_TASKRUN, completions arrive from interrupts at unpredictable times
///      instead of being batched into the reactor loop, adding contention.
///   4. Our Handle() loop is designed around submit_and_wait_timeout — it drains new
///      connections, returns buffers, and flushes writes, then submits+waits in one call.
///      SQPOLL breaks this model because the kernel thread submits SQEs as soon as they
///      appear, before the reactor has finished batching its work for the iteration.
///
/// SQPOLL shines for storage I/O (NVMe) workloads with extremely high SQE submission
/// rates where the submit syscall itself is the bottleneck and spare cores are available.
/// </summary>
internal static class SqPollExample
{
    const uint IORING_SETUP_SQPOLL = 1u << 1;  // kernel polls SQ
    const uint IORING_SETUP_SQ_AFF = 1u << 2;  // pin SQPOLL thread to CPU

    internal static Engine CreateEngine(int reactorCount = 12)
    {
        var reactorConfigs = Enumerable.Range(0, reactorCount).Select(i => new ReactorConfig(
            RingFlags: IORING_SETUP_SQPOLL | IORING_SETUP_SQ_AFF,
            SqCpuThread: i,         // pin each SQPOLL thread to its own core
            SqThreadIdleMs: 100
        )).ToArray();

        return new Engine(new EngineOptions
        {
            Port = 8080,
            ReactorCount = reactorCount,
            ReactorConfigs = reactorConfigs
        });
    }

    internal static async Task HandleConnectionAsync(Connection connection)
    {
        while (true)
        {
            var result = await connection.ReadAsync();
            if (result.IsClosed)
                break;

            var rings = connection.GetAllSnapshotRingsAsUnmanagedMemory(result);

            // Process received data as ReadOnlySequence / spans...
            var sequence = rings.ToReadOnlySequence();

            // Return kernel buffers
            foreach (var ring in rings)
                connection.ReturnRing(ring.BufferId);

            // Write response
            connection.Write(
                "HTTP/1.1 200 OK\r\nContent-Length: 13\r\nContent-Type: text/plain\r\n\r\nHello, World!"u8);

            await connection.FlushAsync();
            connection.ResetRead();
        }
    }
}
