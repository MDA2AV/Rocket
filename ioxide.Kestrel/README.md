# ioxide.Kestrel

An ASP.NET Core **Kestrel transport** backed by the [ioxide](https://github.com/MDA2AV/ioxide) io_uring
runtime. One reactor (io_uring ring) per core, SO_REUSEPORT load-balanced, with Kestrel's entire request
loop pinned to the reactor thread — no ThreadPool hop on the hot path.

## Usage

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseIoxide();   // replaces Kestrel's default sockets transport

var app = builder.Build();
app.MapGet("/", () => "Hello, World!");
app.Run();
```

Options:

```csharp
builder.WebHost.UseIoxide(o =>
{
    o.ReactorCount = Environment.ProcessorCount;          // rings/threads (default: ProcessorCount)
    o.ConfigureServer = cfg => cfg with { RingEntries = 8192 };   // tune the underlying ioxide ServerConfig
});
```

## How it works

Each accepted connection is bridged to Kestrel through a `System.IO.Pipelines` duplex whose **reader
schedulers route continuations onto the owning reactor thread**. A recv pump copies received bytes into
the inbound pipe and a send pump drains Kestrel's response into the connection's send slab, so
`recv → HTTP parse → handler → send` all run on a single ring thread.

## Requirements

- Linux with io_uring (kernel 6.x recommended).
- .NET 11.

> Inline execution note: like any thread-per-core transport, application middleware runs on the reactor
> thread. Blocking work in a handler stalls every connection on that reactor — keep handlers async.
