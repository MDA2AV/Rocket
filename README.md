[![ioxide](https://img.shields.io/nuget/v/ioxide?label=ioxide)](https://www.nuget.org/packages/ioxide/)
[![ioxide.httpclient](https://img.shields.io/nuget/v/ioxide.httpclient?label=ioxide.httpclient)](https://www.nuget.org/packages/ioxide.httpclient/)
[![ioxide.pg](https://img.shields.io/nuget/v/ioxide.pg?label=ioxide.pg)](https://www.nuget.org/packages/ioxide.pg/)
[![ioxide.redis](https://img.shields.io/nuget/v/ioxide.redis?label=ioxide.redis)](https://www.nuget.org/packages/ioxide.redis/)
[![ioxide.file](https://img.shields.io/nuget/v/ioxide.file?label=ioxide.file)](https://www.nuget.org/packages/ioxide.file/)
[![ioxide.Kestrel](https://img.shields.io/nuget/v/ioxide.Kestrel?label=ioxide.Kestrel)](https://www.nuget.org/packages/ioxide.Kestrel/)
[![ioxide.ngtcp2](https://img.shields.io/nuget/v/ioxide.ngtcp2?label=ioxide.ngtcp2)](https://www.nuget.org/packages/ioxide.ngtcp2/)
[![ioxide.nghttp3](https://img.shields.io/nuget/v/ioxide.nghttp3?label=ioxide.nghttp3)](https://www.nuget.org/packages/ioxide.nghttp3/)

**A shared-nothing io_uring runtime for .NET.**

One ring per reactor thread - run one per core. Each reactor owns its ring, its SO_REUSEPORT
listener, its connections and its clients outright: nothing is shared, so nothing is locked.
HTTP, Postgres, Redis and file I/O all submit on the owning ring and resume inline on the
same thread. No thread pool on the hot path. No native dependencies - raw syscalls, nothing else.

> Linux 6.1+ · .NET 10 / .NET 11 · status `0.2.6` - experimental

**[Documentation](https://mda2av.github.io/ioxide/)** - architecture, guides, and every example
below as runnable code you can read side by side.

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

## Clients ride the same ring

Every client is opened from the reactor's start hook, which runs on the reactor thread - so the
connections it makes belong to that reactor's ring. The handler fetches it back as a reactor
service. There is one pool per reactor and no sharing between them, which is why none of it needs
a lock.

That holds for a Postgres query, a Redis command, an outbound HTTP call and a positional file
read alike: each is submitted on the ring that accepted the request and resumes on the same
thread, so a request never leaves the core it arrived on. A reverse proxy built this way keeps
both hops - inbound connection and outbound call - on one thread for the life of the request.

## Scope

ioxide hands you raw bytes and stays out of HTTP. Request parsing and response bytes are your
code; the runtime owns the ring, the connections and the clients. When you want a framework on
top, `ioxide.Kestrel` swaps the transport under an existing ASP.NET Core app - one ring per core,
with Kestrel's request loop pinned to the reactor thread, and your endpoints unchanged.

## Packages

| Package | What it does |
| --- | --- |
| `ioxide` | The runtime: reactors, TCP/UDP transports, connections, the ring-native client seam, and TLS termination (OpenSSL handshake over the ring, then kTLS - handlers keep writing plaintext). |
| `ioxide.httpclient` | The ring-native HTTP client: HTTP/1.1, HTTP/2 (h2c) and HTTP/3 behind one API, Alt-Svc negotiation, one set of message types. Bundles the nghttp2 native. |
| `ioxide.ngtcp2` | QUIC engine: ngtcp2 + picotls bundled native. Only system dependency is OpenSSL 3. |
| `ioxide.nghttp3` | HTTP/3 + QPACK (nghttp3), bundled native. Rides any QUIC connection. |
| `ioxide.http3` | Pure-C# HTTP/3: frames, QPACK, Huffman. Zero native code, drop-in for `ioxide.nghttp3`. |
| `ioxide.pg` | Postgres driver. A pool per reactor; connect, query and stream rows on the owning ring. |
| `ioxide.redis` | Redis client. RESP2, pipelining, pub/sub - pooled per reactor. |
| `ioxide.file` | Static assets. Immutable snapshots, baked responses, positional ring reads. |
| `ioxide.Kestrel` | ASP.NET Core transport: `UseIoxide()` and Kestrel runs one ring per core. |

## Where the code is

Nothing here is pseudocode. Every pattern above exists as something you can run:

| | What you get |
| --- | --- |
| **[Documentation](https://mda2av.github.io/ioxide/)** | The examples browser - TCP, QUIC, HTTP/3, every client, and the ASP.NET drop-in, side by side. |
| **[Playground](Playground/)** | One project per workload. Each `Program.cs` is a **complete** server - config, reactors, threads, handler - so you can copy the file out and run it. |
| **[Examples.AspNet](Examples.AspNet/)** | `UseIoxide()` measured against a stock Kestrel baseline. |
