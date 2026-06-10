# ioxide — a thread-per-core io_uring runtime for .NET

> One ring per core. HTTP, Postgres, and file I/O all submit on that ring and resume **inline** on
> the reactor thread. No thread pool on the hot path. The ring *is* the I/O.

`ioxide` is not another web framework — it's a **runtime**: the reactor, the inline async model, and a
growing set of **ring-native clients** that all ride the same io_uring instance. You write a normal
`async` handler; under it, every `await` (an HTTP read, a Postgres query, a file read) is an io_uring
op that completes as a CQE on *your* reactor and resumes your code right where it left off.

## The thesis

.NET's async I/O is built on the thread pool + a shared socket engine (epoll). It's excellent and
general — but for the highest-throughput, lowest-latency services it means every I/O hops threads.
The thread-per-core + io_uring model (glommio, tokio-current-thread, seastar) doesn't exist in .NET
as a cohesive runtime. `ioxide` is that runtime.

Measured on this stack (32-core box), all hand-rolled, all NativeAOT-friendly:
- **HTTP**: ~3.5–4M req/s inline (Minima/rhythm) — at or above Tokio on the same machine.
- **Postgres over the ring**: ~58K req/s @ 20µs single-conn, **zero thread-pool / socket-engine threads**.

These aren't projections — the engine, the Postgres client, and a runnable host all build and run
today (`Playground`).

## The spine: `IRingHost`

Everything composes through one contract the reactor implements:

```csharp
void Bind(int fd, IRingCompletion target);

void SubmitSend (int fd, nint buffer, int length);                // sockets
void SubmitRecv (int fd, nint buffer, int length);
void SubmitRead (int fd, nint buffer, int length, long offset);   // files
void SubmitWrite(int fd, nint buffer, int length, long offset);
```

A client (Postgres, a file, Redis, an HTTP upstream) hands the reactor a buffer + fd; the reactor
owns the ring and routes the completion back to whichever client bound that fd. **One abstraction,
many clients.** Add a capability by writing a client against `IRingHost` — never by touching the
reactor.

The application meets the engine through three more seams on the reactor: a **`Handle`** delegate
(the per-connection request handler), an **`OnStart`** hook (open ring-native clients on the reactor
thread, before serving), and a **`State`** slot (stash a per-reactor client for the handler to use).
The engine never names a client type — which is exactly what keeps `ioxide.pg` and `ioxide.file`
*downstream* of `ioxide`.

## What's here vs. what's next

| Capability | Status | Where |
|---|---|---|
| Reactor / HTTP engine (io_uring, IVTS recv/flush, thread-per-core) | ✅ | `ioxide` (ported from `Research/Minima`) |
| `IRingHost` — the seam every client rides | ✅ | `ioxide` |
| Postgres over the ring (inline resume) | ✅ | `ioxide.pg` |
| Files over the ring (`IORING_OP_READ` at offset, Fractal-style) | ✅ | `ioxide.file` |
| Runnable host — plaintext, and `db=42` over the ring | ✅ | `Playground` |
| Connection pool — many in-flight queries per reactor | ⬜ | — |
| BCL bridge — SyncContext escape hatch for arbitrary async libs | ⬜ | prototype in `Research/loom` |
| File route in the host · SCRAM auth · extended protocol · Redis · TLS | ⬜ | — |

> `ioxide` does **not** reference `Research/`. The research engines are where these ideas were proven; the
> `ioxide.*` projects are a clean, self-contained re-home of that work against the `IRingHost` seam.

## The honest challenge (and the strategy)

The hard part isn't performance — it's the **ecosystem**. .NET's async world (Npgsql, EF, HttpClient,
gRPC) is built on the thread pool; an inline io_uring runtime can't use any of it directly. Reimplementing
all of it is a multi-year island.

So the strategy is **not** "reimplement everything":

1. **Hand-roll the highest-value clients** — HTTP server, Postgres, file I/O, Redis. That covers the
   bulk of real backends, inline and thread-pool-free.
2. **Keep a BCL escape hatch** — a per-reactor `SynchronizationContext` lets a handler `await` *any*
   .NET async library; the work runs on the pool, the continuation marshals home to the reactor. You
   pay a round-trip for that call, but you're **never blocked**. Inline-fast where it matters,
   compatible everywhere else.

That hybrid — ring-native fast path + BCL-compatible long tail — is what makes it adoptable rather
than a research toy.

## Who it's for

Services where you control the stack and want maximum throughput / minimum tail latency: edge
proxies, API gateways, high-fan-out backends, latency-sensitive systems. Not a Kestrel replacement
for line-of-business apps — a specialized runtime for when the I/O model is the bottleneck.

## Layout

```
ioxide/            ← the engine: the io_uring reactor (implements IRingHost) + the seam
  IRingHost.cs       the contract: Bind + Send/Recv (sockets) + Read/Write (files)
  Reactor/           Reactor.cs · Reactor.Incremental.cs · Reactor.RingHost.cs
  Connection/  io_uring/  Utils/  ServerConfig.cs
ioxide.pg/   ← Postgres over the ring: its own wire protocol + connection   (→ ioxide)
ioxide.file/      ← files over the ring: IORING_OP_READ at an offset             (→ ioxide)
Playground/     ← a host that wires it together: serves "ok", or "db=42"       (→ all three)
ioxide.slnx        ← the four projects; nothing from Research/

Research/       ← the experimental engines this was distilled from — ioxide does NOT depend on it
RingPg.md       ← the original Postgres-driver deep-dive (Research/RingPg + Minima)
```

The reactor implements `IRingHost`; each `ioxide.*` client is written purely against it, so the engine
never depends on its clients. Run it:

```bash
# plaintext
dotnet run -c Release --project Playground                    # GET / → ok

# Postgres over the ring (needs a trust-auth PG on :5432, user/db "bench")
PLAYGROUND_DB=1 dotnet run -c Release --project Playground     # GET / → db=42
```

— status: `0.0.1`
