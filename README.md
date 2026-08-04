[![ioxide](https://img.shields.io/nuget/v/ioxide?label=ioxide)](https://www.nuget.org/packages/ioxide/)
[![ioxide.httpclient](https://img.shields.io/nuget/v/ioxide.httpclient?label=ioxide.httpclient)](https://www.nuget.org/packages/ioxide.httpclient/)
[![ioxide.pg](https://img.shields.io/nuget/v/ioxide.pg?label=ioxide.pg)](https://www.nuget.org/packages/ioxide.pg/)
[![ioxide.redis](https://img.shields.io/nuget/v/ioxide.redis?label=ioxide.redis)](https://www.nuget.org/packages/ioxide.redis/)
[![ioxide.file](https://img.shields.io/nuget/v/ioxide.file?label=ioxide.file)](https://www.nuget.org/packages/ioxide.file/)
[![ioxide.tls](https://img.shields.io/nuget/v/ioxide.tls?label=ioxide.tls)](https://www.nuget.org/packages/ioxide.tls/)
[![ioxide.Kestrel](https://img.shields.io/nuget/v/ioxide.Kestrel?label=ioxide.Kestrel)](https://www.nuget.org/packages/ioxide.Kestrel/)
[![ioxide.ngtcp2](https://img.shields.io/nuget/v/ioxide.ngtcp2?label=ioxide.ngtcp2)](https://www.nuget.org/packages/ioxide.ngtcp2/)
[![ioxide.nghttp3](https://img.shields.io/nuget/v/ioxide.nghttp3?label=ioxide.nghttp3)](https://www.nuget.org/packages/ioxide.nghttp3/)

**A shared-nothing io_uring runtime for .NET.**

One ring per reactor thread - run one per core. Each reactor owns its ring, its SO_REUSEPORT
listener, its connections and its clients outright: nothing is shared, so nothing is locked.
HTTP, Postgres, Redis and file I/O all submit on the owning ring and resume inline on the
same thread. No thread pool on the hot path. No native dependencies - raw syscalls, nothing else.

> Linux 6.1+ · .NET 10 / .NET 11 · status `0.2.6` - experimental

**[Documentation](https://mda2av.github.io/ioxide/)** - architecture, guides, the full picture

## Hello, ring

```csharp
using ioxide;
using ioxide.utils;

var config = new ServerConfig
{
    ReactorCount = Environment.ProcessorCount,
    Tcp = new TcpOptions { Port = 8080 },
};

// One reactor per core. Every one binds :8080 via SO_REUSEPORT and owns its own ring.
for (int i = 0; i < config.ReactorCount; i++)
{
    var reactor = new Reactor(i, config);

    reactor.TcpHandle = async (r, conn) =>
    {
        try
        {
            while (true)
            {
                // io_uring recv. Resumes inline on this reactor thread - no thread pool hop.
                RecvSnapshot snapshot = await conn.ReadAsync();

                // ioxide hands you raw bytes; parsing is your code. Here we just drain,
                // returning each buffer to the ring.
                while (conn.TryGetItem(snapshot, out SpscRecvRing.Item item))
                {
                    if (item.HasBuffer) conn.ReturnBuffer(in item);
                }

                conn.Write("HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok"u8);
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

    new Thread(reactor.Run).Start();
}
```

```bash
dotnet add package ioxide
curl http://localhost:8080/
```

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

Every client is opened from `OnStart`, which runs on the reactor thread - so the connections it
makes belong to that reactor's ring. `Start` registers the client as a reactor service; the handler
fetches it with `GetService<T>()`. There is one pool per reactor and no sharing between them.

### Postgres

```csharp
using ioxide.pg;

reactor.OnStart = r => PgPool.Start(r, new PgOptions
{
    Host = "127.0.0.1", Port = 5432, User = "bench", Database = "bench",
    PoolSize = 4,                 // per reactor
});

reactor.TcpHandle = async (r, conn) =>
{
    PgPool pg = r.GetService<PgPool>();

    // Submitted on this reactor's ring; the continuation resumes on this thread.
    PgResult result = await pg.QueryAsync("SELECT 42");
    // result.Value  ->  "42"
};
```

### Redis

```csharp
using ioxide.redis;

reactor.OnStart = r => RedisPool.Start(r, new RedisOptions
{
    Host = "127.0.0.1", Port = 6379, PoolSize = 8,
});

reactor.TcpHandle = async (r, conn) =>
{
    RedisPool redis = r.GetService<RedisPool>();

    await redis.ExecuteAsync("SET", "user:1", "ada");
    string? name = await redis.GetAsync("user:1");   // "ada"

    // The generic command surface reaches anything RESP2 speaks.
    RespValue hits = await redis.ExecuteAsync("INCR", "hits");
};
```

### HTTP client

`ioxide.httpclient` is one client per origin over HTTP/1.1, HTTP/2 (h2c) or HTTP/3. Requests and
responses are the same types whichever protocol serves them, so the protocol is a configuration
decision, not an API one. Both hops of a proxy - inbound and outbound - stay on one reactor thread.

```csharp
using ioxide.http11;      // HttpClientRequest / HttpClientResponse live here
using ioxide.httpclient;

reactor.OnStart = r => RingHttpClient.Start(r, new RingHttpClientOptions
{
    Host = "127.0.0.1", Port = 8081,
    PoolSize = 8,

    // Start on HTTP/1.1 and switch to HTTP/3 once the origin advertises it via Alt-Svc.
    // Http1Only / Http2Only (h2c, prior knowledge) / Http3Only pin it instead.
    Policy = HttpProtocolPolicy.Negotiate,
});

reactor.TcpHandle = async (r, conn) =>
{
    RingHttpClient http = r.GetService<RingHttpClient>();

    using HttpClientResponse response = await http.GetAsync("/api/thing");
    int status = response.Status;              // 200
    ReadOnlyMemory<byte> body = response.Body; // bytes, not a string - decode at the edge
};
```

### Static files

The asset cache opens every file once and shares the descriptors across reactors; small files are
served from a pre-baked HTTP response with no I/O at all, larger ones stream off the ring. Every hit
is revalidated against disk (size + mtime + inode), so an edit or an atomic rename is served live
rather than stale.

```csharp
using ioxide.file;

var assets = new StaticAssets("/srv/www", maxCachedFileBytes: 256 * 1024);

reactor.OnStart = r =>
{
    r.AddService(assets);
    AssetReader.CreatePool(r, readers: 4, bufferBytes: 1 << 20);
};

reactor.TcpHandle = async (r, conn) =>
{
    StaticAssets snapshot = r.GetService<StaticAssets>();

    // The lease pins the snapshot for the whole request, so a concurrent reload
    // can't free the fd mid-send.
    using StaticAssets.Lease lease = snapshot.Acquire();
    if (lease.TryGet("/index.html", out AssetCache.Asset asset))
    {
        // asset.Response is the baked HTTP response; asset.Fd reads off the ring.
    }
};
```

`assets.Reload()` swaps in a fresh snapshot atomically - the old descriptors close after a grace
period, so in-flight requests finish on the bytes they started with.

## HTTP/3

`ioxide.ngtcp2` bundles ngtcp2 + picotls as one self-contained native library (TLS 1.3 lives inside
the transport). `ioxide.nghttp3` puts real HTTP/3 on top. `Nghttp3Connection` owns the read loop -
QPACK, control streams, fin and teardown are its problem - and calls your function once per request.

```csharp
using ioxide.ngtcp2;
using ioxide.nghttp3;

var engine = new QuicEngine("cert.pem", "key.pem", cidLength: 8, alpn: ["h3"]);

var config = new ServerConfig
{
    ReactorCount = Environment.ProcessorCount,
    Udp  = new UdpOptions { RecvSlots = 16 },
    Quic = new QuicOptions
    {
        Port = 8443,                            // every reactor binds it via SO_REUSEPORT
        LocalCidLength = 8,
        ConnectionFactory = engine.CreateFactory(),
    },
};

// The request is post-QPACK BYTES throughout - route by byte compare, decode only at the edge.
reactor.QuicHandle = (r, conn) =>
    new Nghttp3Connection(conn).RunBufferedAsync(static req =>
        req.Path.Span.SequenceEqual("/plaintext"u8)
            ? Nghttp3Response.Text("Hello, World!")
            : Nghttp3Response.Text("not found\n", status: 404));
```

```bash
curl --http3-only -k https://localhost:8443/plaintext
```

For large or hostile uploads, `RunStreamingAsync` dispatches at end-of-headers and pulls the body
through `req.BodyReader` under flow-control pacing - memory is bound by one window, not the body
size. `ioxide.http3` is the same surface implemented in pure C# (frames + QPACK + Huffman, no native
h3 code), engine-agnostic over any QUIC connection.

## ASP.NET Core

Already have an app? `ioxide.Kestrel` swaps the transport underneath it - one ring per core, with
Kestrel's request loop pinned to the reactor thread. Your endpoints do not change.

```csharp
using ioxide.Kestrel;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseIoxide(o => o.ReactorCount = 16);

var app = builder.Build();
app.MapGet("/", () => "hello from io_uring");
app.Run();
```

## Packages

| Package | What it does |
| --- | --- |
| `ioxide` | The runtime: reactors, TCP/UDP transports, connections, the ring-native client seam. |
| `ioxide.http11` | HTTP message types and the ring-native HTTP/1.1 client. No native dependencies. |
| `ioxide.nghttp2` | HTTP/2 + HPACK (nghttp2), bundled native. Sans-I/O - bytes ride any ioxide connection. |
| `ioxide.ngtcp2` | QUIC engine: ngtcp2 + picotls bundled native. Only system dependency is OpenSSL 3. |
| `ioxide.nghttp3` | HTTP/3 + QPACK (nghttp3), bundled native. Rides any QUIC connection. |
| `ioxide.http3` | Pure-C# HTTP/3: frames, QPACK, Huffman. Zero native code, drop-in for `ioxide.nghttp3`. |
| `ioxide.tls` | TLS. OpenSSL handshake over the ring, then kTLS - handlers keep writing plaintext. |
| `ioxide.httpclient` | One HTTP client per origin over 1.1 / h2c / h3, chosen per origin via Alt-Svc. |
| `ioxide.pg` | Postgres driver. A pool per reactor; connect, query and stream rows on the owning ring. |
| `ioxide.redis` | Redis client. RESP2, pipelining, pub/sub - pooled per reactor. |
| `ioxide.file` | Static assets. Immutable snapshots, baked responses, positional ring reads. |
| `ioxide.Kestrel` | ASP.NET Core transport: `UseIoxide()` and Kestrel runs one ring per core. |

## Scope

ioxide hands you raw bytes and stays out of HTTP. Request parsing and response bytes are your
code; the runtime owns the ring, the connections and the clients. When you want a framework
on top, `ioxide.Kestrel` plugs the same engine under ASP.NET Core.

## Try it

The [Playground](Playground/) is one runnable project per workload — `Raw`, `Pg`, `File`, `Proxy`,
three HTTP/3 flavors, and the synthetic `Pipe`/`Hop`/`TaskRun` variants — each a small `Program.cs`
over a shared library:

```bash
PLAYGROUND_REACTORS=4 dotnet run -c Release --project Playground/Raw
```

The [Examples project](Examples/) builds every snippet above as runnable code, with
[benchmark results](Examples/RESULTS.md).
