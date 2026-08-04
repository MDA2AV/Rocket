# Playground

A host for the ioxide engine. One process, one mode, N reactor threads — each mode swaps in a
different handler so the same engine can be pointed at a different workload.

```bash
PLAYGROUND_MODE=raw PLAYGROUND_REACTORS=4 dotnet run -c Release --project Playground
curl http://127.0.0.1:8080/
```

Linux only — the engine is io_uring.

## Layout

| File | What lives there |
| --- | --- |
| `Program.cs` | Wires the resolved mode to reactor threads. Nothing mode-specific. |
| `PlaygroundConfig.cs` | Every `PLAYGROUND_*` knob, read once at startup. |
| `Modes.cs` | The mode table: handlers, per-reactor services, QUIC and drain requirements. |
| `Http/ConnectionLoop.cs` | The read/respond/repeat loop every TCP mode shares. |
| `Http/RequestParser.cs` | Draining the recv ring; pulling the target out of the request line. |
| `Http/Responses.cs` | Canned responses and the byte-level writers. |
| `Handlers/` | One file per workload. |
| `Setup/` | Self-signed QUIC cert, sample asset directory. |

Adding a mode means adding one row to `Modes.Resolve` — the row declares whether it needs a QUIC
listener and whether shutdown has to drain nghttp3, so there is no second place to keep in sync.

## Modes

`PLAYGROUND_MODE` picks one. An unrecognised name falls back to `raw` with a warning.

| Mode | What it does |
| --- | --- |
| `raw` | Fixed plaintext response, no I/O beyond the socket. The throughput baseline. |
| `pipe` | Same workload through the `PipeReader`/`PipeWriter` adapters, to price the adapter. |
| `hop` | Same, but every request bounces through the thread pool — exercises the off-reactor queues and the eventfd wake. |
| `taskrun` | Same, but each request awaits a `Task.Run` JSON serialization. Logs once if the continuation resumes off-reactor. |
| `pg` | A `PgPool` per reactor. `/` → `SELECT 42` · `/sleep` → 100 ms · `/hang` → 10 s · `/err` → server error → 500. |
| `file` | Static files over the shared asset cache: small assets from the baked response, large ones read off the ring. Misses are 404. |
| `proxy` | Forwards every request to an upstream origin through `ioxide.http11` and relays status and body back. |
| `quic`, `h3` | HTTP/3 via nghttp3, **streamed** dispatch — the handler runs while the body is still arriving, paced by the flow-control window. |
| `h3-buffered` | HTTP/3 via nghttp3, **buffered** dispatch — the whole body is in `request.Body` before the handler runs, and the handler may still await. |
| `http3` | HTTP/3 via the pure-C# `ioxide.http3` stack (frames + QPACK + Huffman, no native h3 code). |

The QUIC modes still listen on TCP `:8080` alongside UDP `:8443`.

### HTTP/3 routes (`quic` / `h3`)

| Path | Shows |
| --- | --- |
| `/plaintext` | A response instance built once and reused — zero allocations per request. |
| `/upload` | Streamed request body; memory bound is one flow-control window, not the body size. |
| `/headers` | Walks `req.Headers.AsSpan()` — the `KeyValueList`, no strings. |
| `/cookies` | `req.TryGetCookie` plus `req.Cookies`, and sets one back via `set-cookie`. |
| `/1k` | A fixed 1 KiB body, for comparability with load-generator grids. |
| anything else | Hello, decoding the path only because it goes into text. |

`h3-buffered` serves `/plaintext` and `/upload`; `http3` serves `/upload`. Both answer hello elsewhere.

## Environment

### Engine

| Variable | Default | Meaning |
| --- | --- | --- |
| `PLAYGROUND_MODE` | `raw` | Which mode to run. |
| `PLAYGROUND_REACTORS` | `12` | Reactor threads; each binds the port via `SO_REUSEPORT`. |
| `PLAYGROUND_PORT` | `8080` | TCP listen port. |
| `PLAYGROUND_UDP_SLOTS` | `16` | UDP recv slots per reactor. |
| `PLAYGROUND_INCREMENTAL` | unset | `1` enables incremental recv. |

### raw / pipe / hop / taskrun

| Variable | Default | Meaning |
| --- | --- | --- |
| `PLAYGROUND_BODY` | `2` | Response body size in bytes (`2` is `"ok"`). Set `1024` to match the object size load-generator grids conventionally measure. Non-positive values fall back to `2`. |

### file

| Variable | Default | Meaning |
| --- | --- | --- |
| `PLAYGROUND_DIR` | `/tmp/ioxide-assets` | Asset root. Seeded with a sample `index.html` and `style.css` if empty. |
| `PLAYGROUND_CACHE_MAX` | `262144` | Per-file byte ceiling for pinning bodies in memory. `0` forces every request through the ring-read path. |

Send `SIGHUP` (`kill -HUP <pid>`) to reload: a fresh snapshot is opened and swapped in atomically,
and the old descriptors close after a grace period.

### quic / h3 / h3-buffered / http3

| Variable | Default | Meaning |
| --- | --- | --- |
| `PLAYGROUND_QUIC_PORT` | `8443` | UDP listen port. |
| `PLAYGROUND_QUIC_CERT` | generated | Cert path. Unset generates a self-signed `CN=localhost` pair under the temp directory. |
| `PLAYGROUND_QUIC_KEY` | generated | Key path. Must be set together with `_CERT`. |
| `PLAYGROUND_QPACK_CAP` | `0` | QPACK dynamic table capacity for the nghttp3 modes; `>0` also advertises 100 blocked streams. `0` is static-only, nghttp3's default. |

`SIGTERM` GOAWAYs every live nghttp3 connection, waits 2 s for in-flight requests, then exits —
without it the process dies mid-request and clients see resets. This covers `quic`, `h3` and
`h3-buffered`; `http3` holds no nghttp3 session, so there is nothing to GOAWAY.

### pg

| Variable | Default |
| --- | --- |
| `PLAYGROUND_PG_HOST` | `127.0.0.1` |
| `PLAYGROUND_PG_PORT` | `5432` |
| `PLAYGROUND_PG_USER` | `bench` |
| `PLAYGROUND_PG_DB` | `bench` |
| `PLAYGROUND_PG_POOL` | `4` (per reactor) |
| `PLAYGROUND_PG_TIMEOUT` | `30000` ms |

### proxy

| Variable | Default |
| --- | --- |
| `PLAYGROUND_UPSTREAM_HOST` | `127.0.0.1` |
| `PLAYGROUND_UPSTREAM_PORT` | `8081` |
| `PLAYGROUND_UPSTREAM_POOL` | `8` (per reactor) |

## Docker

```bash
docker build -f Playground/Dockerfile -t playground .   # from the repo root
docker run --rm -p 8080:8080 -e PLAYGROUND_MODE=raw playground
```

The QUIC modes also need `-p 8443:8443/udp`. io_uring pins memory, so a container running many
reactors may need `--ulimit memlock=-1:-1`.
