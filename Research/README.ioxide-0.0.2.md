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

Measured on this stack, all hand-rolled, all NativeAOT-friendly (2 reactors, loopback, kernel 6.17):
- **HTTP**: ~760K req/s plaintext; ~660K req/s with per-connection incremental buffer rings.
- **Postgres over the ring**: ~350K req/s with a real `SELECT 42` on every request, through
  4 pooled connections per reactor — and ~600K req/s even when every request detours through the
  thread pool and back (the escape-hatch path).

These aren't projections — the engine, the pooled Postgres client, and a runnable host all build and
run today (`Playground`).

## The spine: `IRingHost`

Everything composes through one contract the reactor implements:

```csharp
void SubmitConnect(int fd, nint sockaddr, int len, IRingCompletion c);   // sockets
void SubmitSend   (int fd, nint buffer, int len, IRingCompletion c);
void SubmitRecv   (int fd, nint buffer, int len, IRingCompletion c);
void SubmitRead   (int fd, nint buffer, int len, long offset, IRingCompletion c);   // files
void SubmitWrite  (int fd, nint buffer, int len, long offset, IRingCompletion c);
```

A client hands the reactor an fd, a buffer, and **the completion to call back**; the reactor owns
the ring and routes each CQE to its op's completion — in O(1), via a slot table, with no
registration around descriptor lifetimes. Because routing is **per operation** rather than per fd,
two clients (or two concurrent ops of one client) can be in flight on the same descriptor without
colliding. Submissions are safe from any thread: off-reactor callers are marshalled home through
the reactor's wake queue. **One abstraction, many clients.** Add a capability by writing a client
against `IRingHost` — never by touching the reactor.

On top of the seam sits a small **client kit** (in `ioxide`, still engine-side and client-agnostic):

- `RingOpSource` — the reusable awaitable for one op (the IVTS plumbing, written once);
- `RingSocket` — a client TCP socket whose *connect*, sends, and recvs all ride the ring
  (full-duplex capable: one source per direction);
- `RingPool<T>` — rent/return with a FIFO waiter queue, the pattern that turns one-conversation
  clients into per-reactor concurrency.

The application meets the engine through three more seams on the reactor: a **`Handle`** delegate
(the per-connection request handler), an **`OnStart`** hook (open ring-native clients on the reactor
thread, before serving), and typed **services** (`AddService<T>` / `GetService<T>`) so one reactor
can carry any number of clients — a `PgPool`, an asset-reader pool — without the engine naming any
client type. That's exactly what keeps `ioxide.pg` and `ioxide.file` *downstream* of `ioxide`.

## What's here vs. what's next

| Capability | Status | Where |
|---|---|---|
| Reactor / HTTP engine (io_uring, IVTS recv/flush, thread-per-core) | ✅ | `ioxide` (ported from `Research/Minima`) |
| `IRingHost` + client kit — the seam every client rides | ✅ | `ioxide` (`Client/`) |
| Postgres over the ring (pooled, inline resume, ring-native connect) | ✅ | `ioxide.pg` |
| Connection pool — many in-flight queries per reactor | ✅ | `ioxide.pg` (`PgPool` on `RingPool<T>`) |
| Files over the ring (`IORING_OP_READ` at offset, pooled readers) | ✅ | `ioxide.file` |
| Runnable host — plaintext, `db=42`, static files, off-reactor demo | ✅ | `Playground` |
| BCL bridge — SyncContext escape hatch for arbitrary async libs | ⬜ | prototype in `Research/loom` (the marshalled submit path already works — see `hop` mode) |
| SCRAM auth · extended protocol (prepared statements) · Redis · TLS | ⬜ | — |
| Fixed files / direct descriptors · send-zerocopy · recv bundles | ⬜ | perf roadmap |

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
   compatible everywhere else. (The reactor side of this — submits and flushes from foreign
   threads — already works; `hop` mode serves ~600K req/s through it.)

That hybrid — ring-native fast path + BCL-compatible long tail — is what makes it adoptable rather
than a research toy.

## Who it's for

Services where you control the stack and want maximum throughput / minimum tail latency: edge
proxies, API gateways, high-fan-out backends, latency-sensitive systems. Not a Kestrel replacement
for line-of-business apps — a specialized runtime for when the I/O model is the bottleneck.

## Layout

```
ioxide/            ← the engine: the io_uring reactor (implements IRingHost) + the seam
  IRingHost.cs       the contract: per-op submit + completion
  Client/            the kit: RingOpSource · RingSocket · RingPool<T>
  Reactor/           Reactor.cs · Reactor.Incremental.cs · Reactor.RingHost.cs (slot table + services)
  Connection/  io_uring/  Utils/  ServerConfig.cs
ioxide.pg/      ← Postgres over the ring: PgPool · PgConnection · PgProtocol      (→ ioxide)
ioxide.file/    ← files over the ring: RingFile · AssetReader · AssetCache        (→ ioxide)
Playground/     ← a host that wires it together: raw / pg / file / hop modes      (→ all three)
ioxide.slnx     ← the four projects; nothing from Research/

Research/       ← the experimental engines this was distilled from — ioxide does NOT depend on it
RingPg.md       ← the original Postgres-driver deep-dive (Research/RingPg + Minima)
```

The reactor implements `IRingHost`; each `ioxide.*` client is written purely against it, so the engine
never depends on its clients. Run it:

```bash
# plaintext
dotnet run -c Release --project Playground                     # GET / → ok

# Postgres over the ring (needs a trust-auth PG, user/db "bench")
PLAYGROUND_MODE=pg dotnet run -c Release --project Playground  # GET /      → db=42
                                                               # GET /sleep → pool concurrency demo
                                                               # GET /err   → ErrorResponse → 500
# env: PLAYGROUND_PG_HOST/PORT/USER/DB/POOL

# static files off the ring (SIGHUP reloads the asset snapshot atomically)
PLAYGROUND_MODE=file dotnet run -c Release --project Playground

# every request detours via the thread pool — the off-reactor/marshalling path
PLAYGROUND_MODE=hop dotnet run -c Release --project Playground

# per-connection incremental buffer rings (kernel 6.12+)
PLAYGROUND_INCREMENTAL=1 dotnet run -c Release --project Playground
```

— status: `0.0.2`
