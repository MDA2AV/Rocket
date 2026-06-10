namespace ioxide;

/// <summary>
/// ioxide — a thread-per-core io_uring runtime for .NET.
///
/// One ring per core. HTTP, Postgres, and file I/O all submit on that ring and resume inline on
/// the reactor thread; nothing is handed to the thread pool on the hot path. The whole runtime is
/// organised around one seam, <see cref="IRingHost"/>: submit an operation on a descriptor, and be
/// called back when its completion lands. Every capability — a database, a file, a cache — is just
/// a client written against that seam.
///
/// The reactor that implements the seam lives in this project (ported from Minima). The
/// <c>ioxide.pg</c> and <c>ioxide.file</c> clients are written purely against
/// <see cref="IRingHost"/>, and <c>Playground</c> wires a reactor, a handler, and a connection into
/// a running server. This type is the composition root in waiting — a future builder API will wrap
/// the hand-wiring the Playground does today.
/// </summary>
public static class IoxideRuntime
{
    /// <summary>A coarse version marker; real entry points arrive as the runtime fills in.</summary>
    public const string Version = "0.0.1";

    // How the Playground wires it today (a builder API will eventually wrap this):
    //
    //     for each core:
    //         var reactor = new Reactor(id, config);              // implements IRingHost
    //         reactor.Handle  = HandleConnection;                 // the request handler
    //         reactor.OnStart = r => r.State =                    // open clients on this thread
    //             PgConnection.Connect(r, "bench", "bench");      //   ioxide.pg — rides the ring
    //         reactor.Run();                                      // handler awaits the query inline
    //
    // For any library that only knows the BCL thread-pool model (Npgsql, EF, HttpClient), a
    // per-reactor SynchronizationContext is the escape hatch: the work runs on the pool, and only
    // the continuation is marshalled home to the reactor. Inline where it matters; compatible
    // everywhere else.
    //
    // Roadmap:
    //   [x] Reactor / HTTP engine     — io_uring, thread-per-core (this project, from Minima)
    //   [x] IRingHost                 — the seam
    //   [x] ioxide.pg              — Postgres over the ring
    //   [x] ioxide.file                 — files over the ring
    //   [x] Playground                — a running host (plaintext + db=42)
    //   [ ] Connection pool           — many in-flight queries per reactor
    //   [ ] BCL bridge                — the SyncContext escape hatch
    //   [ ] A builder API             — replace the Playground's hand-wiring
}
