using ioxide;

namespace ioxide.timer;

/// <summary>
/// A wait that runs on the host's ring, the way a <see cref="RingSocket"/>'s reads do - the
/// building block for anything that has to hold a deadline without leaving the reactor.
///
/// The deadline travels in the submission, so the kernel holds it and the completion arrives on
/// the reactor thread with the connection's state still warm. Nothing is armed on the side and
/// no syscall is made to arrange it: the SQE goes out with whatever batch the reactor was
/// already sending.
///
/// One op in flight per timer, like <see cref="RingOpSource"/> - hold one per connection, or one
/// per thing that waits. Zero allocation per wait once constructed.
///
/// Results follow the ring rather than the caller's intuition: a wait that runs its course
/// completes with <c>-ETIME</c> (-62), which is io_uring reporting the timeout expired and is
/// the success case here. Any other negative value is an errno.
/// </summary>
public sealed class RingTimer
{
    /// <summary>io_uring's report for a timeout that expired normally.</summary>
    public const int ETime = -62;

    private const long NanosecondsPerMillisecond = 1_000_000L;

    private readonly IRingHost _host;
    private readonly RingOpSource _source = new();

    public RingTimer(IRingHost host)
    {
        _host = host;
    }

    /// <summary>Completes once <paramref name="milliseconds"/> have elapsed.</summary>
    public ValueTask<int> DelayAsync(int milliseconds)
    {
        return DelayNanosecondsAsync(milliseconds * NanosecondsPerMillisecond);
    }

    /// <summary>Completes once <paramref name="delay"/> has elapsed.</summary>
    public ValueTask<int> DelayAsync(TimeSpan delay)
    {
        return DelayNanosecondsAsync((long)(delay.TotalMilliseconds * NanosecondsPerMillisecond));
    }

    /// <summary>
    /// Completes once <paramref name="nanoseconds"/> have elapsed. A duration at or below zero
    /// becomes the smallest the kernel will take, because a zero timespec disarms a timer rather
    /// than firing one.
    /// </summary>
    public ValueTask<int> DelayNanosecondsAsync(long nanoseconds)
    {
        ValueTask<int> pending = _source.Prepare();
        _host.SubmitTimeout(nanoseconds < 1 ? 1 : nanoseconds, _source);
        return pending;
    }

    /// <summary>True for a completion that is the timer expiring rather than an error.</summary>
    public static bool Expired(int result)
    {
        return result == ETime || result == 0;
    }
}
