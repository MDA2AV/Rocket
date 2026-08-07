# Playground

One project per sample, and **each `Program.cs` is a complete ioxide server you can copy out and
run**. The config, the reactors, the threads, the connection loop and the handler are all there in
the file - nothing that touches an ioxide API is hidden behind a helper.

Run any of them with `dotnet run -c Release --project Playground/<Group>/<Name>`, then hit
`http://127.0.0.1:8080/`. Linux only - the engine is io_uring.

## Samples

| Project | Lines | Shows | Packages |
| --- | --- | --- | --- |
| [`Tcp.Raw`](Tcp/Raw/Program.cs) | 88 | The whole shape: `ServerConfig`, a reactor per core, the read/respond/`DecRef` loop. Start here. | `ioxide` |
| [`Tcp.Pipe`](Tcp/Pipe/Program.cs) | 77 | The same server through `PipeReader`/`PipeWriter`, if your code already speaks Pipelines. | `ioxide` |
| [`Tcp.Hop`](Tcp/Hop/Program.cs) | 76 | Leaving the reactor on purpose (`Task.Yield`) and coming back - what it costs. | `ioxide` |
| [`Tcp.TaskRun`](Tcp/TaskRun/Program.cs) | 84 | Awaiting ordinary thread-pool work and still resuming on the reactor. | `ioxide` |
| [`Tcp.Incremental`](Tcp/Incremental/Program.cs) | 95 | `Tcp.Raw` with the per-connection buffer-ring mode - one config block is the whole diff. Kernel 6.12+. | `ioxide` |
| [`Tcp.Big`](Tcp/Big/Program.cs) | 101 | The write path under a 100 KB body: `SEND_ZC`, slab overflow (`grow` vs `seg`), checksum-able output. | `ioxide` |
| [`Tls.Ktls`](Tls/Ktls/Program.cs) | 133 | OpenSSL handshake on the ring, then **kernel TLS** transmit - the handler writes plaintext. | `ioxide` |
| [`Tls.SslStream`](Tls/SslStream/Program.cs) | 100 | The BCL `SslStream` over `TcpConnectionStream` - portable userspace TLS, the kTLS comparison point. | `ioxide` |
| [`Http2.Nghttp2`](Http2/Nghttp2/Program.cs) | 66 | HTTP/2 (h2c, prior knowledge) with nghttp2 doing framing, HPACK and flow control. | `ioxide.nghttp2` |
| [`Http2.Managed`](Http2/Managed/Program.cs) | 66 | The same server with **zero native code** - framing, HPACK and flow control in C#. Drop-in for the above. | `ioxide.http2` |
| [`Http2.Tls`](Http2/Tls/Program.cs) | 132 | h2 **and** http/1.1 on one port, chosen by ALPN. The HTTP/2 code is unchanged - only the pipe differs. | `ioxide.nghttp2` |
| [`Http2.SslStream`](Http2/SslStream/Program.cs) | 100 | The same HTTP/2 over the BCL `SslStream`, via a ten-line `Stream`-to-`IDuplexPipe` adapter. | `ioxide.nghttp2` |
| [`Http3.Nghttp3`](Http3/Nghttp3/Program.cs) | 169 | HTTP/3 with **streamed** dispatch, and a `SIGTERM` GOAWAY drain. | `ioxide.ngtcp2`, `ioxide.nghttp3` |
| [`Http3.Buffered`](Http3/Buffered/Program.cs) | 146 | The same server with **buffered** dispatch - one method call is the whole difference. | `ioxide.ngtcp2`, `ioxide.nghttp3` |
| [`Quic.Alpn`](Quic/Alpn/Program.cs) | 111 | One QUIC listener, two protocols by ALPN: h3, or raw stream echo over the dual pipe. QUIC-only - `Tcp = null`. | `ioxide.ngtcp2`, `ioxide.nghttp3` |
| [`Proxy.H1ToH1`](Proxy/H1ToH1/Program.cs) | 131 | A reverse proxy, whole - both hops on one reactor thread. Read this one first. | `ioxide.httpclient` |
| [`Proxy.H1ToH2`](Proxy/H1ToH2/Program.cs) | 132 | The same frontend, h2 upstream: `PoolSize` 1 carries every concurrent request. | `ioxide.httpclient` |
| [`Proxy.H1ToH3`](Proxy/H1ToH3/Program.cs) | 131 | h1 in, h3 out - with no `Quic` config at all: the first connect opens an ephemeral client socket. | `ioxide.httpclient` |
| [`Proxy.H2ToH1`](Proxy/H2ToH1/Program.cs) | 112 | The classic edge: clients get multiplexing, the origin keeps h1. The one combination whose pool must size for concurrency. | `ioxide.nghttp2`, `ioxide.httpclient` |
| [`Proxy.H2ToH2`](Proxy/H2ToH2/Program.cs) | 103 | h2 both sides - two sockets per reactor whatever the load. Two HPACK tables that cannot be spliced. | `ioxide.nghttp2`, `ioxide.httpclient` |
| [`Proxy.H2ToH3`](Proxy/H2ToH3/Program.cs) | 105 | The protocol-translating edge: TCP in, QUIC out, no server-side QUIC config. | `ioxide.nghttp2`, `ioxide.httpclient` |
| [`Proxy.H3ToH1`](Proxy/H3ToH1/Program.cs) | 106 | HTTP/3 front door, HTTP/1.1 upstream - QUIC-only frontend, keep-alive h1 pool behind. | `ioxide.ngtcp2`, `ioxide.nghttp3`, `ioxide.httpclient` |
| [`Proxy.H3ToH2`](Proxy/H3ToH2/Program.cs) | 103 | The same proxy with the pool swapped: requests multiplex onto one h2c upstream connection. | `ioxide.ngtcp2`, `ioxide.nghttp3`, `ioxide.httpclient` |
| [`Proxy.H3ToH3`](Proxy/H3ToH3/Program.cs) | 105 | h3 on both sides: the upstream QUIC connections share the serving socket - one fd, both directions. | `ioxide.ngtcp2`, `ioxide.nghttp3`, `ioxide.httpclient` |
| [`Clients.Pg`](Clients/Pg/Program.cs) | 181 | A `PgPool` per reactor: scalar queries, prepared params (`/add`, `/upper`), row streaming (`/rows`), errors and timeouts. | `ioxide.pg` |
| [`Clients.Redis`](Clients/Redis/Program.cs) | 194 | A `RedisPool` per reactor: GET hot path, cache-aside, RESP types, explicit pipelining. | `ioxide.redis` |
| [`Clients.File`](Clients/File/Program.cs) | 253 | Static files: baked responses, ring reads, disk revalidation, `SIGHUP` reload. | `ioxide.file` |
| [`Clients.Https`](Clients/Https/Program.cs) | 160 | Calling an `https://` origin: SNI, ALPN and certificate verification on the client side. | `ioxide.httpclient` |

Read `Tcp.Raw` first - every other sample is that same skeleton with one thing changed.

Each project references only the packages it demonstrates: `Tcp.Raw` publishes three assemblies and
no native libraries at all, while `Http3.Nghttp3` pulls in the ngtcp2 and nghttp3 bundles. So the build
graph, not a comment, decides what each sample is allowed to touch.

## What is shared, and why so little

[`Shared`](Shared) holds three files and **does not reference ioxide at all**:

| File | Why it is not in the samples |
| --- | --- |
| `Env.cs` | `PLAYGROUND_*` parsing. Noise; you would use your own config. |
| `QuicCert.cs` | Generating a self-signed localhost cert. X509 boilerplate, nothing to do with ioxide. |
| `SampleAssets.cs` | Writing a demo `index.html` so `Clients.File` has something to serve. |

Everything else is duplicated across samples **on purpose**. The read/respond/`DecRef` loop appears
in eight files because it is the ioxide idiom - the thing you came here to copy. Factoring it into a
shared `ServeAsync` would make the Playground shorter and make it useless.

## HTTP/3

Both samples answer every request with a single reused response object, so the handler stays small
enough to read at a glance. `Http3.Nghttp3` reads the request body through `BodyReader` while it is still
arriving; `Http3.Buffered` gets it complete in `request.Body`. That one difference is the reason
both exist.

They also listen on TCP `:8080` alongside UDP `:8443`, and answer
`curl --http3-only -k https://127.0.0.1:8443/`.

## Environment

### Engine - every sample

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

`SIGTERM` GOAWAYs every live nghttp3 connection, waits 2 s, then exits - without it the process dies
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

Needs a Postgres to talk to - the defaults match a `postgres:18` container started with
`POSTGRES_USER=bench`, `POSTGRES_DB=bench` and `POSTGRES_HOST_AUTH_METHOD=trust`. Without one it
answers `500`, which is the error path working.

### Proxy

| Variable | Default |
| --- | --- |
| `PLAYGROUND_UPSTREAM_HOST` | `127.0.0.1` |
| `PLAYGROUND_UPSTREAM_PORT` | `8081` |
| `PLAYGROUND_UPSTREAM_POOL` | `8` h1 upstream, `32` for `H2ToH1`, `1` for any h2/h3 upstream |

Nine samples, one per frontend x upstream combination: the frontend protocol is the server type
(`Nghttp2Connection`, `Nghttp3Connection`, or a raw TCP loop) and the upstream protocol is the
pool type (`HttpClientPool`, `Http2ClientPool`, `Http3ClientPool`). Nothing else differs, which
is the point.

Each needs an origin to forward to - run one of the servers above on `PLAYGROUND_UPSTREAM_PORT`
in another terminal. With nothing listening they answer `502` once the acquire timeout elapses.

## Docker

`Playground/Dockerfile` builds one image per sample, selected with `--build-arg SAMPLE=<path>` from
the repo root - `Tcp/Raw`, `Clients.Pg`, `Http3.Nghttp3` and so on, matching the directory layout. Publish
`8080/tcp`, and `8443/udp` as well for the HTTP/3 samples.

io_uring pins memory, so a container running many reactors may need `--ulimit memlock=-1:-1`.
