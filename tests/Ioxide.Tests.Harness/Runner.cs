namespace Ioxide.Tests;

/// <summary>Tiny test runner: PASS / FAIL / SKIP per test, a summary line, and a non-zero exit on failure.</summary>
public sealed class Runner
{
    /// <summary>
    /// How long any one test may run before it is called hung. Generous, because several tests
    /// wait out acquire budgets and cooldowns on purpose - this is a backstop against a wedged
    /// read, not a performance bar.
    /// </summary>
    private const int DefaultTimeoutMs = 120_000;

    private int _passed;
    private int _failed;
    private int _skipped;

    public void Test(string name, Action body, bool skip = false, int timeoutMs = DefaultTimeoutMs)
    {
        if (skip)
        {
            Console.WriteLine($"SKIP  {name}");
            _skipped++;
            return;
        }

        try
        {
            RunWithWatchdog(name, body, timeoutMs);
            Console.WriteLine($"PASS  {name}");
            _passed++;
        }
        catch (Exception e)
        {
            Console.WriteLine($"FAIL  {name}: {e.Message}");
            _failed++;
        }
        finally
        {
            // Servers do not outlive the test that started them. A reactor busy-polls its ring, so
            // leaving them up means a long suite ends with dozens of them competing for the box -
            // and the tests that notice are whichever ones happen to be timing-sensitive, which
            // makes it look like a bug in whatever they were testing.
            TestServer.StopAll();
        }
    }

    /// <summary>
    /// Run the body on a worker and give up on it after <paramref name="timeoutMs"/>. Test bodies
    /// are synchronous, so one that wedges - a read that never completes, an acquire that never
    /// resolves - would otherwise hang the whole suite behind the CI job timeout and report
    /// nothing at all. A hung test is now one FAIL and the rest still run.
    /// </summary>
    /// <remarks>
    /// The worker is abandoned rather than aborted, because .NET cannot abort a thread and killing
    /// a reactor mid-operation would corrupt every later test anyway. It is a background thread, so
    /// it cannot keep the process alive past the summary.
    /// </remarks>
    private static void RunWithWatchdog(string name, Action body, int timeoutMs)
    {
        Exception? failure = null;
        using var finished = new ManualResetEventSlim(false);

        var worker = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception e)
            {
                failure = e;
            }
            finally
            {
                finished.Set();
            }
        })
        {
            IsBackground = true,
            Name = $"test-{name}",
        };
        worker.Start();

        if (!finished.Wait(timeoutMs))
        {
            throw new TimeoutException(
                $"timed out after {timeoutMs} ms (the test is wedged; later tests still ran)");
        }

        if (failure is not null)
        {
            // Capture/Throw, not `throw failure` - the latter resets the stack to this line, which
            // discards where the test actually broke.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    public int Summary()
    {
        // A reactor can die after the test that started it has already passed - in a ticker, a
        // sweep, a later handler - and every assertion still succeeds. Reporting green in that case
        // is worse than the unhandled-exception crash this harness replaced, so any death nobody
        // consumed fails the run here.
        IReadOnlyList<string> unreported = TestServer.DrainUnreportedFailures();
        foreach (string failure in unreported)
        {
            Console.WriteLine($"FAIL  a test reactor died and no test observed it: {failure}");
            _failed++;
        }

        Console.WriteLine($"\n{_passed} passed, {_failed} failed, {_skipped} skipped");
        return _failed == 0 ? 0 : 1;
    }
}

public static class Assert
{
    public static void Equal<T>(T expected, T actual)
    {
        if (!Equals(expected, actual))
        {
            throw new Exception($"expected [{expected}], got [{actual}]");
        }
    }

    public static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception(message);
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> and requires it to throw <typeparamref name="T"/>, optionally
    /// with <paramref name="because"/> somewhere in the message.
    /// </summary>
    /// <remarks>
    /// The reason to pass a fragment: a test that only asserts "it threw" passes when something
    /// else entirely went wrong - a port already bound, a reactor that died - and reports the
    /// refusal it was looking for. Naming the reason is what makes the test about the reason.
    /// </remarks>
    public static void Throws<T>(Action action, string? because = null) where T : Exception
    {
        try
        {
            action();
        }
        catch (T e)
        {
            if (because is not null && !e.Message.Contains(because))
            {
                throw new Exception($"threw {typeof(T).Name} as expected, but for the wrong reason: {e.Message}");
            }

            return;
        }
        catch (Exception e)
        {
            throw new Exception($"expected {typeof(T).Name}, got {e.GetType().Name}: {e.Message}");
        }

        throw new Exception($"expected {typeof(T).Name}{(because is null ? "" : $" ({because})")}, but nothing was thrown");
    }
}
