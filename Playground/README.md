# Playground

One runnable project per workload. Each is a small `Program.cs` over a shared library, so you can
read any single sample end to end without stepping around the other nine.

```bash
dotnet run -c Release --project Playground/Raw
curl http://127.0.0.1:8080/
```

Linux only — the engine is io_uring.

## Samples

| Project | What it demonstrates |
| --- | --- |
| [`Raw`](Raw) | A fixed plaintext response, no I/O beyond the socket. The throughput baseline. |
| [`Pipe`](Pipe) | The same workload through the `PipeReader`/`PipeWriter` adapters, to price the adapter. |
| [`Hop`](Hop) | The same, but every request bounces through the thread pool — exercises the off-reactor queues and the eventfd wake. |
| [`TaskRun`](TaskRun) | The same, but each request awaits a `Task.Run`. Logs once if the continuation resumes off-reactor. |
| [`Pg`](Pg) | A `PgPool` per reactor. `/` → `SELECT 42` · `/sleep` → 100 ms · `/hang` → 10 s · `/err` → 500. |
| [`File`](File) | Static files over the shared asset cache: baked responses for small files, ring reads for large. `SIGHUP` reloads. |
| [`Proxy`](Proxy) | Forwards every request to an upstream through `ioxide.http11`. Both hops stay on one reactor thread. |
| [`H3`](H3) | HTTP/3 via nghttp3, **streamed** dispatch — the handler runs while the body arrives, paced by flow control. |
| [`H3Buffered`](H3Buffered) | HTTP/3 via nghttp3, **buffered** dispatch — the whole body is in `req.Body` before the handler runs. |
| [`Http3`](Http3) | HTTP/3 via the **pure-C#** `ioxide.http3` stack. Deliberately does not reference `ioxide.nghttp3`. |

Each project references only the packages it demonstrates. `Raw` publishes 3 assemblies and no
native libraries; `Http3` pulls in ngtcp2 for the QUIC transport but no nghttp3, which is the whole
point of comparing it against `H3`.

The HTTP/3 samples also listen on TCP `:8080` alongside UDP `:8443`.

## Shared projects

| Project | Contents |
| --- | --- |
| [`Shared`](Shared) | `PlaygroundHost` (config → reactors → run), the `ConnectionLoop` every TCP sample shares, request parsing, canned responses, `Env`. References ioxide core only. |
| [`Shared.Quic`](Shared.Quic) | `QuicSetup` — self-signed cert, ngtcp2 engine, listener options. Used by all three HTTP/3 samples. |
| [`Shared.Nghttp3`](Shared.Nghttp3) | QPACK settings, the live-connection registry behind the `SIGTERM` GOAWAY, and the reusable response constants. Used by `H3` and `H3Buffered` only. |

`Shared.Quic` and `Shared.Nghttp3` are separate so the pure-C# `Http3` sample can take the QUIC
transport without dragging in the native h3 layer it exists to replace.

A sample declares itself as a `PlaygroundSample` and hands it to `PlaygroundHost.Run` — handlers,
per-reactor services, and any signal hooks. That is the whole contract:

```csharp
return PlaygroundHost.Run(new PlaygroundSample
{
    Name = "raw",
    Summary = "fixed plaintext response",
    Tcp = (reactor, conn) => ConnectionLoop.ServeAsync(conn, new FixedResponder(response)),
});
```

## HTTP/3 routes

`H3` serves the full set; `H3Buffered` serves `/plaintext` and `/upload`; `Http3` serves `/upload`.
All three answer hello elsewhere.

| Path | Shows |
| --- | --- |
| `/plaintext` | A response instance built once and reused — zero allocations per request. |
| `/upload` | Streamed request body; memory bound is one flow-control window, not the body size. |
| `/headers` | Walks `req.Headers.AsSpan()` — the `KeyValueList`, no strings. |
| `/cookies` | `req.TryGetCookie` plus `req.Cookies`, and sets one back via `set-cookie`. |
| `/1k` | A fixed 1 KiB body, for comparability with load-generator grids. |

```bash
curl --http3-only -k https://127.0.0.1:8443/plaintext
```

## Environment

### Engine — every sample

| Variable | Default | Meaning |
| --- | --- | --- |
| `PLAYGROUND_REACTORS` | `12` | Reactor threads; each binds the port via `SO_REUSEPORT`. |
| `PLAYGROUND_PORT` | `8080` | TCP listen port. |
| `PLAYGROUND_UDP_SLOTS` | `16` | UDP recv slots per reactor. |
| `PLAYGROUND_INCREMENTAL` | unset | `1` enables incremental recv. |

### Raw · Pipe · Hop · TaskRun (and the TCP port of the HTTP/3 samples)

| Variable | Default | Meaning |
| --- | --- | --- |
| `PLAYGROUND_BODY` | `2` | Response body size in bytes (`2` is `"ok"`). Set `1024` to match the object size load-generator grids conventionally measure. Non-positive values fall back to `2`. |

### File

| Variable | Default | Meaning |
| --- | --- | --- |
| `PLAYGROUND_DIR` | `/tmp/ioxide-assets` | Asset root. Seeded with a sample `index.html` and `style.css` if empty. |
| `PLAYGROUND_CACHE_MAX` | `262144` | Per-file byte ceiling for pinning bodies in memory. `0` forces every request through the ring-read path. |

`kill -HUP <pid>` reloads: a fresh snapshot is opened and swapped in atomically, and the old
descriptors close after a grace period.

### H3 · H3Buffered · Http3

| Variable | Default | Meaning |
| --- | --- | --- |
| `PLAYGROUND_QUIC_PORT` | `8443` | UDP listen port. |
| `PLAYGROUND_QUIC_CERT` | generated | Cert path. Unset generates a self-signed `CN=localhost` pair under the temp directory. |
| `PLAYGROUND_QUIC_KEY` | generated | Key path. Must be set together with `_CERT`. |
| `PLAYGROUND_QPACK_CAP` | `0` | QPACK dynamic table capacity, `H3`/`H3Buffered` only; `>0` also advertises 100 blocked streams. `0` is static-only, nghttp3's default. |

`SIGTERM` GOAWAYs every live nghttp3 connection, waits 2 s for in-flight requests, then exits —
without it the process dies mid-request and clients see resets. That covers `H3` and `H3Buffered`;
`Http3` holds no nghttp3 session, so there is nothing to GOAWAY.

### Pg

| Variable | Default |
| --- | --- |
| `PLAYGROUND_PG_HOST` | `127.0.0.1` |
| `PLAYGROUND_PG_PORT` | `5432` |
| `PLAYGROUND_PG_USER` | `bench` |
| `PLAYGROUND_PG_DB` | `bench` |
| `PLAYGROUND_PG_POOL` | `4` (per reactor) |
| `PLAYGROUND_PG_TIMEOUT` | `30000` ms |

### Proxy

| Variable | Default |
| --- | --- |
| `PLAYGROUND_UPSTREAM_HOST` | `127.0.0.1` |
| `PLAYGROUND_UPSTREAM_PORT` | `8081` |
| `PLAYGROUND_UPSTREAM_POOL` | `8` (per reactor) |

Run an origin for it to forward to:

```bash
PLAYGROUND_PORT=8081 dotnet run -c Release --project Playground/Raw   # terminal 1
dotnet run -c Release --project Playground/Proxy                      # terminal 2
```

## Docker

One image per sample, selected at build time:

```bash
docker build -f Playground/Dockerfile --build-arg SAMPLE=Raw -t playground-raw .
docker run --rm -p 8080:8080 playground-raw

docker build -f Playground/Dockerfile --build-arg SAMPLE=H3 -t playground-h3 .
docker run --rm -p 8080:8080 -p 8443:8443/udp playground-h3
```

io_uring pins memory, so a container running many reactors may need `--ulimit memlock=-1:-1`.
