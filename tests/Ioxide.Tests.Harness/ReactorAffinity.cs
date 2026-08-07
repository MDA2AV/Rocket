using ioxide;

namespace Ioxide.Tests;

/// <summary>
/// Records whether a handler is still running on the reactor that owns its ring, at points the
/// test picks.
///
/// The invariant this exists to protect: <b>ioxide's own code must never be the reason a handler
/// leaves its reactor.</b> The runtime does support off-reactor callers - <c>SubmitClientOp</c>
/// hands them to <c>_remoteOps</c> and wakes the ring - but that path is an escape hatch for USER
/// code that deliberately goes cross-thread. When one of ioxide's own seams trips it, every client
/// call on that connection silently becomes a queue push plus an eventfd write plus a reactor
/// wake-up, for the life of the connection, and nothing anywhere reports it.
///
/// The check is deliberately <see cref="Reactor.OnReactorThread"/> - the very predicate
/// <c>SubmitClientOp</c> branches on - rather than a thread name or a SynchronizationContext, so
/// a passing test means precisely "the marshal path would not have been taken here".
/// </summary>
public sealed class ReactorAffinity
{
    private readonly object _gate = new();
    private readonly List<string> _drift = [];
    private int _checks;

    /// <summary>
    /// Assert-and-record. Never throws: a handler that threw here would look like a connection
    /// fault and the test would report the wrong thing.
    /// </summary>
    public void Check(Reactor reactor, string where)
    {
        lock (_gate)
        {
            _checks++;

            if (reactor.OnReactorThread)
            {
                return;
            }

            Thread thread = Thread.CurrentThread;
            _drift.Add($"{where}: tid={Environment.CurrentManagedThreadId} "
                     + $"name={thread.Name ?? "<none>"} pool={thread.IsThreadPoolThread} "
                     + $"ctx={SynchronizationContext.Current?.GetType().Name ?? "<null>"}");
        }
    }

    /// <summary>How many observations were taken - zero means the handler never ran.</summary>
    public int Checks
    {
        get { lock (_gate) { return _checks; } }
    }

    /// <summary>Every point that ran off-reactor, for the failure message.</summary>
    public IReadOnlyList<string> Drift
    {
        get { lock (_gate) { return _drift.ToArray(); } }
    }
}
