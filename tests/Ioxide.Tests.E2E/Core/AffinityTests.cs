using System.Text;
using ioxide;

namespace Ioxide.Tests;

/// <summary>
/// Continuation affinity: after any await inside a handler, execution is back on the reactor that
/// started it.
///
/// The mechanism is a <see cref="ReactorSynchronizationContext"/> installed on the reactor thread,
/// so an await that is NOT one of ioxide's own - Task.Delay, Task.Run, a BCL HttpClient call -
/// resumes on the reactor instead of a thread-pool thread. That matters beyond tidiness: the
/// ring's submission queue and every connection's state are single-threaded by construction, and a
/// handler that came back on a pool thread would be touching both from the wrong one.
///
/// This shipped untested. What follows is the acceptance criteria it was specified against.
/// </summary>
internal static class AffinityTests
{
    public static void Register(Runner runner)
    {
        runner.Test("affinity: a reactor thread carries a ReactorSynchronizationContext", () =>
        {
            // The install itself, which everything below depends on.
            int port = TestServer.Start(async (r, conn) =>
            {
                try
                {
                    await conn.ReadAsync();

                    SynchronizationContext? context = SynchronizationContext.Current;
                    bool ours = context is ReactorSynchronizationContext c && ReferenceEquals(c.Reactor, r);

                    Wire.Write(conn, 200, ours ? "installed" : $"wrong context: {context?.GetType().Name ?? "none"}");
                    await conn.FlushAsync();
                }
                finally
                {
                    conn.DecRef();
                }
            });

            (int status, string body) = Client.Get(port, "/");
            Assert.Equal(200, status);
            Assert.Equal("installed", body);
        });

        runner.Test("affinity: Task.Delay resumes on the reactor", () =>
        {
            // A timer completion belongs to the BCL, so without the context this continuation
            // lands on a thread-pool thread.
            AssertBackOnReactorAfter(static async () => await Task.Delay(1));
        });

        runner.Test("affinity: Task.Run resumes on the reactor", () =>
        {
            // The awaited work genuinely runs on the pool; only the CONTINUATION comes home.
            AssertBackOnReactorAfter(static async () => await Task.Run(static () => Thread.Sleep(1)));
        });

        runner.Test("affinity: a BCL HttpClient call resumes on the reactor", () =>
        {
            // The case the context was built for: a handler calling something that knows nothing
            // about ioxide, whose continuation would otherwise migrate off the reactor for good.
            int origin = TestServer.Start(Handlers.Raw);

            AssertBackOnReactorAfter(async () =>
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                await http.GetStringAsync($"http://127.0.0.1:{origin}/");
            });
        });

        runner.Test("affinity: nested awaits all come back", () =>
        {
            // Affinity has to survive the whole chain, not just the first hop - a handler that
            // drifted off on the second await would be just as broken.
            AssertBackOnReactorAfter(static async () =>
            {
                await Task.Delay(1);
                await Task.Run(static () => { });
                await Task.Yield();
            });
        });

        runner.Test("affinity: an async void callback that throws does not kill the reactor", () =>
        {
            // An async void continuation raises on whatever thread resumes it. Once that thread is
            // the reactor, an unguarded throw would unwind Run() and take the whole reactor with
            // it - the #92 failure class. DrainPostQ wraps each callback for exactly this.
            int port = TestServer.Start(async (r, conn) =>
            {
                try
                {
                    RecvSnapshot snapshot = await conn.ReadAsync();
                    string path = Wire.ReadPath(conn, snapshot);

                    if (path == "/boom")
                    {
                        Explode(r);   // async void: throws from a posted continuation
                        Wire.Write(conn, 200, "queued");
                    }
                    else
                    {
                        Wire.Write(conn, 200, "alive");
                    }

                    await conn.FlushAsync();
                }
                finally
                {
                    conn.DecRef();
                }
            });

            (_, string queued) = Client.Get(port, "/boom");
            Assert.Equal("queued", queued);

            // Give the posted continuation time to run and throw on the reactor thread.
            Thread.Sleep(200);

            // The reactor is still serving, which is the whole assertion.
            for (int i = 0; i < 3; i++)
            {
                (int status, string body) = Client.Get(port, "/ping");
                Assert.Equal(200, status);
                Assert.Equal("alive", body);
            }
        });
    }

    // Runs `work` inside a handler and reports whether execution came back to the reactor thread
    // afterwards. The comparison is against the reactor's own view (OnReactorThread), not a thread
    // id the test captured, so it stays true regardless of how the harness starts reactors.
    private static void AssertBackOnReactorAfter(Func<Task> work)
    {
        int port = TestServer.Start(async (r, conn) =>
        {
            try
            {
                await conn.ReadAsync();

                string verdict;
                try
                {
                    await work();
                    verdict = r.OnReactorThread
                        ? "on-reactor"
                        : $"MIGRATED to thread {Environment.CurrentManagedThreadId}";
                }
                catch (Exception e)
                {
                    verdict = $"work failed: {e.Message}";
                }

                Wire.Write(conn, 200, verdict);
                await conn.FlushAsync();
            }
            finally
            {
                conn.DecRef();
            }
        });

        (int status, string body) = Client.Get(port, "/", timeoutMs: 20_000);
        Assert.Equal(200, status);
        Assert.Equal("on-reactor", body);
    }

    // async void on purpose: this is the shape whose exception has nowhere to go but the thread
    // that resumes it.
    private static async void Explode(Reactor reactor)
    {
        await Task.Delay(1);   // resumes on the reactor, via the context
        throw new InvalidOperationException("async void continuation blew up on the reactor thread");
    }
}
