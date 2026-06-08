namespace Loom;

/// <summary>
/// The heart of loom: a SynchronizationContext bound to one reactor. Any await whose
/// continuation is scheduled through this context is funnelled back onto that reactor's
/// thread (Reactor.Post → enqueue + eventfd wake → the reactor runs it in its loop).
///
/// Installed on the reactor thread, so a handler running there captures it at every
/// `await`. Result: the handler can `await` *arbitrary* async work (Task.Run, Npgsql,
/// HttpClient, …) and the continuation always resumes **on the reactor thread**, where
/// it is allowed to submit to the io_uring ring (single-issuer). The async work runs
/// wherever it wants; only the continuation is woven back home.
/// </summary>
internal sealed class LoomSyncContext : SynchronizationContext
{
    private readonly Reactor _reactor;

    public LoomSyncContext(Reactor reactor) => _reactor = reactor;

    public override void Post(SendOrPostCallback d, object? state) => _reactor.Post(d, state);

    public override void Send(SendOrPostCallback d, object? state)
    {
        if (_reactor.OnReactorThread) d(state);
        else _reactor.Post(d, state);   // demo: no blocking wait
    }

    public override SynchronizationContext CreateCopy() => this;
}
