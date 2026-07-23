[![ioxide](https://img.shields.io/nuget/v/ioxide?label=ioxide)](https://www.nuget.org/packages/ioxide/)
[![ioxide.pg](https://img.shields.io/nuget/v/ioxide.pg?label=ioxide.pg)](https://www.nuget.org/packages/ioxide.pg/)
[![ioxide.file](https://img.shields.io/nuget/v/ioxide.file?label=ioxide.file)](https://www.nuget.org/packages/ioxide.file/)
[![ioxide.tls](https://img.shields.io/nuget/v/ioxide.tls?label=ioxide.tls)](https://www.nuget.org/packages/ioxide.tls/)
[![ioxide.redis](https://img.shields.io/nuget/v/ioxide.redis?label=ioxide.redis)](https://www.nuget.org/packages/ioxide.redis/)
[![ioxide.Kestrel](https://img.shields.io/nuget/v/ioxide.Kestrel?label=ioxide.Kestrel)](https://www.nuget.org/packages/ioxide.Kestrel/)
[![ioxide.quic](https://img.shields.io/nuget/v/ioxide.Kestrel?label=ioxide.quic)](https://www.nuget.org/packages/ioxide.quic/)
[![ioxide.h3](https://img.shields.io/nuget/v/ioxide.Kestrel?label=ioxide.h3)](https://www.nuget.org/packages/ioxide.h3/)

**A shared-nothing io_uring runtime for .NET.**

One ring per reactor thread - run one per core. Each reactor owns its ring, its SO_REUSEPORT
listener, its connections and its clients outright: nothing is shared, so nothing is locked.
HTTP, Postgres, Redis and file I/O all submit on the owning ring and resume inline on the
same thread. No thread pool on the hot path. No native dependencies - raw syscalls, nothing else.

> Linux 6.1+ · .NET 10 · status `0.1.1` - experimental

**[Documentation](https://mda2av.github.io/ioxide/)** - architecture, guides, the full picture

## First-class async/await

Shared-nothing runtimes usually ask you to give up the platform's async model. ioxide keeps it:
handlers are ordinary `async Task` code, and `await` works everywhere.

- Ring completions resume their continuations inline on the reactor thread - an awaited recv,
  query or file read picks up exactly where it left off, with no thread pool hop.
- A per-reactor `SynchronizationContext` catches everything that would otherwise escape:
  timers, `HttpClient`, `Task.Run` results - their continuations post back to the owning reactor.
- Ring operations await through reusable, allocation-free awaitables.

You always wake up on your reactor. That is what makes shared-nothing practical in .NET:
connection, pool and handler state stays single-threaded without a lock in sight.

## Packages

| Package | What it does |
| --- | --- |
| `ioxide` | The runtime: reactors, TCP/UDP transports, connections, the ring-native client seam. |
| `ioxide.pg` | Postgres driver. A pool per reactor; connect, query and stream rows on the owning ring. |
| `ioxide.redis` | Redis client. RESP2, pipelining, pub/sub - pooled per reactor. |
| `ioxide.file` | Static assets. Immutable snapshots, baked responses, positional ring reads. |
| `ioxide.tls` | TLS. OpenSSL handshake over the ring, then kTLS - handlers keep writing plaintext. |
| `ioxide.Kestrel` | ASP.NET Core transport: `UseIoxide()` and Kestrel runs one ring per core. |

## Scope

ioxide hands you raw bytes and stays out of HTTP. Request parsing and response bytes are your
code; the runtime owns the ring, the connections and the clients. When you want a framework
on top, `ioxide.Kestrel` plugs the same engine under ASP.NET Core.
