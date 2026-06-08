using Kingslayer.Utils;

namespace Kingslayer;

/// <summary>
/// Multi-reactor HTTP/1.1 server on the Kingslayer reactor. The point of interest is the handler's
/// <c>await Task.Run(...)</c>: the work runs on the thread pool, but the per-reactor
/// <see cref="ReactorSynchronizationContext"/> resumes the continuation back on the owning reactor —
/// no return/flush/recycle queues. Set <c>KS_WORK=N</c> to do N iterations of CPU on the pool per
/// request (0 = pure inline).
/// </summary>
internal static class Program
{
    private static ReadOnlySpan<byte> Response =>
        "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 2\r\nConnection: keep-alive\r\n\r\nok"u8;

    private static readonly int WorkItems = 1;

    private static int Main()
    {
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });

        var config = new ServerConfig
        {
            ReactorCount = 12,
            Handler      = HandleAsync,
        };

        Console.WriteLine($"[Kingslayer] {config.ReactorCount} reactors on :{config.Port}  (KS_WORK={WorkItems})");

        var threads = new Thread[config.ReactorCount];
        for (var i = 0; i < config.ReactorCount; i++)
        {
            var reactor = new Reactor(i, config);
            threads[i] = new Thread(reactor.Run) { Name = $"reactor-{i}", IsBackground = false };
            threads[i].Start();
        }
        foreach (var t in threads)
        {
            t.Join();
        }
        return 0;
    }

    private static int _proofShown;

    private static async Task HandleAsync(Reactor reactor, Connection conn)
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
                        conn.ReturnBuffer(in item);
                    }
                }

                // EXPERIMENT proof (once): the handler resumed OFF the reactor (on a pool thread) and
                // will submit its flush directly from here — no queue, no reactor hand-off.
                if (Interlocked.Exchange(ref _proofShown, 1) == 0)
                {
                    int tid = Environment.CurrentManagedThreadId;
                    Console.WriteLine($"[Kingslayer-MS] handler on thread={tid} reactorThread={reactor.ThreadId} offReactor={tid != reactor.ThreadId}; flushing from here");
                }

                if (WorkItems > 0)
                {
                    await Task.Run(() =>
                    {
                        long s = 0;
                        for (int i = 0; i < WorkItems; i++) s += i;
                        GC.KeepAlive(s);
                    });
                }

                conn.Write(Response);
                await conn.FlushAsync();   // submitted directly from this (pool) thread, under the ring lock

                if (snap.IsClosed)
                {
                    return;
                }
                conn.ResetRead();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[r{reactor.Id}] handler crash on fd={conn.ClientFd}: {ex}");
        }
        finally
        {
            conn.DecRef();
        }
    }
}
