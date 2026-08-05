# Playground

One project per sample, and **each `Program.cs` is a complete ioxide server you can copy out and
run**. The config, the reactors, the threads, the connection loop and the handler are all there in
the file — nothing that touches an ioxide API is hidden behind a helper.

Run any of them with `dotnet run -c Release --project Playground/<Name>`, then hit
`http://127.0.0.1:8080/`. Linux only — the engine is io_uring.

## Samples

| Project | Lines | Shows | Packages |
| --- | --- | --- | --- |
| [`Tcp.Raw`](Tcp/Raw/Program.cs) | 88 | The whole shape: `ServerConfig`, a reactor per core, the read/respond/`DecRef` loop. Start here. | `ioxide` |
| [`Tcp.Pipe`](Tcp/Pipe/Program.cs) | 77 | The same server through `PipeReader`/`PipeWriter`, if your code already speaks Pipelines. | `ioxide` |
| [`Tcp.Hop`](Tcp/Hop/Program.cs) | 76 | Leaving the reactor on purpose (`Task.Yield`) and coming back — what it costs. | `ioxide` |
| [`Tcp.TaskRun`](Tcp/TaskRun/Program.cs) | 84 | Awaiting ordinary thread-pool work and still resuming on the reactor. | `ioxide` |
| [`Tcp.Incremental`](Tcp/Incremental/Program.cs) | 95 | `Tcp.Raw` with the per-connection buffer-ring mode — one config block is the whole diff. Kernel 6.12+. | `ioxide` |
| [`Tcp.Big`](Tcp/Big/Program.cs) | 101 | The write path under a 100 KB body: `SEND_ZC`, slab overflow (`grow` vs `seg`), checksum-able output. | `ioxide` |
| [`Pg`](Pg/Program.cs) | 181 | A `PgPool` per reactor: scalar queries, prepared params (`/add`, `/upper`), row streaming (`/rows`), errors and timeouts. | `ioxide.pg` |
| [`File`](File/Program.cs) | 253 | Static files: baked responses, ring reads, disk revalidation, `SIGHUP` reload. | `ioxide.file` |
| [`Proxy.H1`](Proxy/H1/Program.cs) | 131 | A reverse proxy where both hops stay on one reactor thread. | `ioxide.http11` |
| [`Proxy.H3ToH1`](Proxy/H3ToH1/Program.cs) | 106 | HTTP/3 front door, HTTP/1.1 upstream — QUIC-only frontend, keep-alive h1 pool behind. | `ioxide.ngtcp2`, `ioxide.nghttp3`, `ioxide.http11` |
| [`Proxy.H3ToH2`](Proxy/H3ToH2/Program.cs) | 104 | The same proxy with the pool swapped: requests multiplex onto one h2c upstream connection. | `ioxide.ngtcp2`, `ioxide.nghttp3`, `ioxide.nghttp2` |
| [`Nghttp3`](Nghttp3/Program.cs) | 169 | HTTP/3 with **streamed** dispatch, and a `SIGTERM` GOAWAY drain. | `ioxide.ngtcp2`, `ioxide.nghttp3` |
| [`Nghttp3Buffered`](Nghttp3Buffered/Program.cs) | 146 | The same server with **buffered** dispatch — one method call is the whole difference. | `ioxide.ngtcp2`, `ioxide.nghttp3` |
| [`Quic.Alpn`](Quic/Alpn/Program.cs) | 111 | One QUIC listener, two protocols by ALPN: h3, or raw stream echo over the dual pipe. QUIC-only — `Tcp = null`. | `ioxide.ngtcp2`, `ioxide.nghttp3` |
| [`Redis`](Redis/Program.cs) | 194 | A `RedisPool` per reactor: GET hot path, cache-aside, RESP types, explicit pipelining. | `ioxide.redis` |
| [`Tls.Ktls`](Tls/Ktls/Program.cs) | 133 | OpenSSL handshake on the ring, then **kernel TLS** transmit — the handler writes plaintext. | `ioxide.tls` |
| [`Tls.SslStream`](Tls/SslStream/Program.cs) | 100 | The BCL `SslStream` over `TcpConnectionStream` — portable userspace TLS, the kTLS comparison point. | `ioxide` |

Read `Tcp.Raw` first — every other sample is that same skeleton with one thing changed.

Each project references only the packages it demonstrates: `Tcp.Raw` publishes three assemblies and
no native libraries at all, while `Nghttp3` pulls in the ngtcp2 and nghttp3 bundles. So the build
graph, not a comment, decides what each sample is allowed to touch.

## What is shared, and why so little

[`Shared`](Shared) holds three files and **does not reference ioxide at all**:

| File | Why it is not in the samples |
| --- | --- |
| `Env.cs` | `PLAYGROUND_*` parsing. Noise; you would use your own config. |
| `QuicCert.cs` | Generating a self-signed localhost cert. X509 boilerplate, nothing to do with ioxide. |
| `SampleAssets.cs` | Writing a demo `index.html` so `File` has something to serve. |

Everything else is duplicated across samples **on purpose**. The read/respond/`DecRef` loop appears
in eight files because it is the ioxide idiom — the thing you came here to copy. Factoring it into a
shared `ServeAsync` would make the Playground shorter and make it useless.

## HTTP/3

Both samples answer every request with a single reused response object, so the handler stays small
enough to read at a glance. `Nghttp3` reads the request body through `BodyReader` while it is still
arriving; `Nghttp3Buffered` gets it complete in `request.Body`. That one difference is the reason
both exist.

They also listen on TCP `:8080` alongside UDP `:8443`, and answer
`curl --http3-only -k https://127.0.0.1:8443/`.

## Environment

### Engine — every sample

| Variable | Default | Meaning |
| --- | --- | --- |
| `PLAYGROUND_REACTORS` | `Environment.ProcessorCount` | Reactor threads; each binds the port via `SO_REUSEPORT`. |
| `PLAYGROUND_PORT` | `8080` | TCP listen port. |
| `PLAYGROUND_UDP_SLOTS` | `16` | UDP recv slots per reactor (HTTP/3 samples). |

### Tcp.Raw

| Variable | Default | Meaning |
| --- | --- | --- |
| `PLAYGROUND_BODY` | `2` | Response body size in bytes (`2` is `"ok"`). Set `1024` to match the object size load-generator grids conventionally measure. |

### File

| Variable | Default | Meaning |
| --- | --- | --- |
| `PLAYGROUND_DIR` | `/tmp/ioxide-assets` | Asset root. Seeded with a demo `index.html` and `style.css` if empty. |
| `PLAYGROUND_CACHE_MAX` | `262144` | Per-file byte ceiling for pinning bodies in memory. `0` forces every request through the ring-read path. |

`kill -HUP <pid>` reloads: a fresh snapshot is opened and swapped in atomically, and the old
descriptors close after a grace period.

### Nghttp3 · Nghttp3Buffered

| Variable | Default | Meaning |
| --- | --- | --- |
| `PLAYGROUND_QUIC_PORT` | `8443` | UDP listen port. |
| `PLAYGROUND_QUIC_CERT` | generated | Cert path. Unset generates a self-signed `CN=localhost` pair under the temp directory. |
| `PLAYGROUND_QUIC_KEY` | generated | Key path. Must be set together with `_CERT`. |
| `PLAYGROUND_QPACK_CAP` | `0` | QPACK dynamic table capacity; `>0` also advertises 100 blocked streams. |

`SIGTERM` GOAWAYs every live nghttp3 connection, waits 2 s, then exits — without it the process dies
mid-request and clients see resets. Both samples register that handler themselves, in the file.

### Pg

| Variable | Default |
| --- | --- |
| `PLAYGROUND_PG_HOST` | `127.0.0.1` |
| `PLAYGROUND_PG_PORT` | `5432` |
| `PLAYGROUND_PG_USER` | `bench` |
| `PLAYGROUND_PG_DB` | `bench` |
| `PLAYGROUND_PG_POOL` | `4` (per reactor) |
| `PLAYGROUND_PG_TIMEOUT` | `30000` ms |

Needs a Postgres to talk to — the defaults match a `postgres:18` container started with
`POSTGRES_USER=bench`, `POSTGRES_DB=bench` and `POSTGRES_HOST_AUTH_METHOD=trust`. Without one it
answers `500`, which is the error path working.

### Proxy

| Variable | Default |
| --- | --- |
| `PLAYGROUND_UPSTREAM_HOST` | `127.0.0.1` |
| `PLAYGROUND_UPSTREAM_PORT` | `8081` |
| `PLAYGROUND_UPSTREAM_POOL` | `8` (per reactor) |

Needs an origin to forward to: run `Tcp.Raw` with `PLAYGROUND_PORT=8081` in one terminal and the
proxy in another. With nothing listening it answers `502` once the acquire timeout elapses.

## Docker

`Playground/Dockerfile` builds one image per sample, selected with `--build-arg SAMPLE=<path>` from
the repo root — `Tcp/Raw`, `Pg`, `Nghttp3` and so on, matching the directory layout. Publish
`8080/tcp`, and `8443/udp` as well for the HTTP/3 samples.

io_uring pins memory, so a container running many reactors may need `--ulimit memlock=-1:-1`.
