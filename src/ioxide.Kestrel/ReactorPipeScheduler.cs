using System.IO.Pipelines;
using ioxide;

namespace ioxide.Kestrel;

/// <summary>
/// A <see cref="PipeScheduler"/> that runs pipe continuations on a reactor's loop thread, so the BCL
/// pipes in <see cref="HopDuplexPipe"/> keep Kestrel's read → parse → handle → send loop pinned to the
/// reactor (no ThreadPool hop).
/// </summary>
internal sealed class ReactorPipeScheduler : PipeScheduler
{
    private readonly Reactor _reactor;

    public ReactorPipeScheduler(Reactor reactor) => _reactor = reactor;

    public override void Schedule(Action<object?> action, object? state)
        => _reactor.ScheduleOnReactor(action, state);
}
