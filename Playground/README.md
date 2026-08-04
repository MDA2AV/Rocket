# Playground

One project per sample, and **each `Program.cs` is a complete ioxide server you can copy out and
run**. The config, the reactors, the threads, the connection loop and the handler are all there in
the file — nothing that touches an ioxide API is hidden behind a helper.

```bash
dotnet run -c Release --project Playground/Raw
curl http://127.0.0.1:8080/
```

Linux only — the engine is io_uring.

## Samples

| Project | Lines | Shows | Packages |
| --- | --- | --- | --- |
| [`Raw`](Raw/Program.cs) | 88 | The whole shape: `ServerConfig`, a reactor per core, the read/respond/`DecRef` loop. Start here. | `ioxide` |
| [`Pipe`](Pipe/Program.cs) | 77 | The same server through `PipeReader`/`PipeWriter`, if your code already speaks Pipelines. | `ioxide` |
| [`Hop`](Hop/Program.cs) | 76 | Leaving the reactor on purpose (`Task.Yield`) and coming back — what it costs. | `ioxide` |
| [`TaskRun`](TaskRun/Program.cs) | 84 | Awaiting ordinary thread-pool work and still resuming on the reactor. | `ioxide` |
| [`Pg`](Pg/Program.cs) | 148 | A `PgPool` per reactor, queried on the same ring that accepted the request. | `ioxide.pg` |
| [`File`](File/Program.cs) | 253 | Static files: baked responses, ring reads, disk revalidation, `SIGHUP` reload. | `ioxide.file` |
| [`Proxy`](Proxy/Program.cs) | 131 | A reverse proxy where both hops stay on one reactor thread. | `ioxide.http11` |
| [`H3`](H3/Program.cs) | 234 | HTTP/3 with **streamed** dispatch, byte-level routing, and a `SIGTERM` GOAWAY drain. | `ioxide.ngtcp2`, `ioxide.nghttp3` |
| [`H3Buffered`](H3Buffered/Program.cs) | 153 | The same server with **buffered** dispatch — one method call is the whole difference. | `ioxide.ngtcp2`, `ioxide.nghttp3` |
| [`Http3`](Http3/Program.cs) | 112 | HTTP/3 with **no native h3 code**, on the pure-C# stack. | `ioxide.ngtcp2`, `ioxide.http3` |

Read `Raw` first — the other nine are that same skeleton with one thing changed.

Each project references only the packages it demonstrates, and that is load-bearing rather than
tidiness: `Http3` publishes `libioxide_ngtcp2.so` and **no** nghttp3, so its "pure C#" claim is
enforced by the build graph. `Raw` publishes three assemblies and no native libraries at all.

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

## Common shape

Every sample is this, with one part swapped:

```csharp
var config = new ServerConfig
{
    ReactorCount = Environment.ProcessorCount,
    Tcp = new TcpOptions { Port = 8080 },
};

var threads = new Thread[config.ReactorCount];

for (int i = 0; i < threads.Length; i++)
{
    var reactor = new Reactor(i, config);

    // Runs ON the reactor thread, so clients opened here ride this reactor's ring.
    reactor.OnStart = r => PgPool.Start(r, pgOptions);

    reactor.TcpHandle = async (r, conn) =>
    {
        try
        {
            while (true)
            {
                RecvSnapshot snapshot = await conn.ReadAsync();   // io_uring recv, resumes inline
                // ... your bytes ...
                conn.Write(response);
                await conn.FlushAsync();

                if (snapshot.IsClosed) return;
                conn.ResetRead();
            }
        }
        finally
        {
            conn.DecRef();
        }
    };

    threads[i] = new Thread(reactor.Run) { Name = $"reactor-{i}" };
    threads[i].Start();
}

foreach (Thread thread in threads) thread.Join();
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

The HTTP/3 samples also listen on TCP `:8080` alongside UDP `:8443`.

## Environment

### Engine — every sample

| Variable | Default | Meaning |
| --- | --- | --- |
| `PLAYGROUND_REACTORS` | `Environment.ProcessorCount` | Reactor threads; each binds the port via `SO_REUSEPORT`. |
| `PLAYGROUND_PORT` | `8080` | TCP listen port. |
| `PLAYGROUND_UDP_SLOTS` | `16` | UDP recv slots per reactor (HTTP/3 samples). |

### Raw

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

### H3 · H3Buffered · Http3

| Variable | Default | Meaning |
| --- | --- | --- |
| `PLAYGROUND_QUIC_PORT` | `8443` | UDP listen port. |
| `PLAYGROUND_QUIC_CERT` | generated | Cert path. Unset generates a self-signed `CN=localhost` pair under the temp directory. |
| `PLAYGROUND_QUIC_KEY` | generated | Key path. Must be set together with `_CERT`. |
| `PLAYGROUND_QPACK_CAP` | `0` | QPACK dynamic table capacity, `H3`/`H3Buffered` only; `>0` also advertises 100 blocked streams. |

`SIGTERM` GOAWAYs every live nghttp3 connection, waits 2 s, then exits — without it the process dies
mid-request and clients see resets. `Http3` holds no nghttp3 session, so it has nothing to drain.

### Pg

| Variable | Default |
| --- | --- |
| `PLAYGROUND_PG_HOST` | `127.0.0.1` |
| `PLAYGROUND_PG_PORT` | `5432` |
| `PLAYGROUND_PG_USER` | `bench` |
| `PLAYGROUND_PG_DB` | `bench` |
| `PLAYGROUND_PG_POOL` | `4` (per reactor) |
| `PLAYGROUND_PG_TIMEOUT` | `30000` ms |

```bash
docker run --rm -d -p 5432:5432 -e POSTGRES_USER=bench -e POSTGRES_DB=bench \
  -e POSTGRES_HOST_AUTH_METHOD=trust postgres:18
dotnet run -c Release --project Playground/Pg
```

### Proxy

| Variable | Default |
| --- | --- |
| `PLAYGROUND_UPSTREAM_HOST` | `127.0.0.1` |
| `PLAYGROUND_UPSTREAM_PORT` | `8081` |
| `PLAYGROUND_UPSTREAM_POOL` | `8` (per reactor) |

```bash
PLAYGROUND_PORT=8081 dotnet run -c Release --project Playground/Raw   # terminal 1: an origin
dotnet run -c Release --project Playground/Proxy                      # terminal 2: the proxy
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
