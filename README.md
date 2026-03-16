[![NuGet](https://img.shields.io/nuget/v/zerg.svg)](https://www.nuget.org/packages/zerg/)

# zerg

Low-level TCP server framework for C# built on Linux `io_uring`. Direct control over sockets, buffers, and scheduling with no hidden abstractions.

**Requirements:** Linux kernel 6.1+, .NET 8/9/10.

## Install

```bash
dotnet add package zerg
```

## Quick Start

```csharp
using zerg.Engine;
using zerg.Engine.Configs;

var engine = new Engine(new EngineOptions { Port = 8080, ReactorCount = 1 });
engine.Listen();

while (engine.ServerRunning)
{
    var connection = await engine.AcceptAsync(CancellationToken.None);
    if (connection is null) continue;
    _ = HandleAsync(connection);
}

static async Task HandleAsync(Connection connection)
{
    while (true)
    {
        var result = await connection.ReadAsync();
        if (result.IsClosed) break;

        var rings = connection.GetAllSnapshotRingsAsUnmanagedMemory(result);
        // process rings.ToReadOnlySequence() ...
        rings.ReturnRingBuffers(connection.Reactor);

        connection.Write("HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nOK"u8);
        await connection.FlushAsync();
        connection.ResetRead();
    }
}
```

## Read API

```csharp
// High-level: get all buffers as a ReadOnlySequence
var result = await connection.ReadAsync();
var rings = connection.GetAllSnapshotRingsAsUnmanagedMemory(result);
ReadOnlySequence<byte> seq = rings.ToReadOnlySequence();
rings.ReturnRingBuffers(connection.Reactor);
connection.ResetRead();

// Low-level: consume one buffer at a time
while (connection.TryGetRing(result.TailSnapshot, out RingItem ring))
{
    ReadOnlySpan<byte> data = ring.AsSpan();
    connection.ReturnRing(ring.BufferId);
}
connection.ResetRead();
```

**Adapters:**

```csharp
// Zero-copy PipeReader (buffers held until AdvanceTo)
var reader = new ConnectionPipeReader(connection);
var result = await reader.ReadAsync();
reader.AdvanceTo(consumed, examined);

// BCL Stream (one copy per read)
var stream = new ConnectionStream(connection);
int n = await stream.ReadAsync(buffer);
```

## Write API

```csharp
connection.Write("data"u8);
await connection.FlushAsync();

// Or via IBufferWriter<byte>
Span<byte> span = connection.GetSpan(256);
connection.Advance(bytesWritten);
await connection.FlushAsync();
```

## Configuration

```csharp
var engine = new Engine(new EngineOptions
{
    Port = 8080,
    ReactorCount = 4,
    AcceptorConfig = new AcceptorConfig(IPVersion: IPVersion.IPv6DualStack),
    ReactorConfigs = Enumerable.Range(0, 4).Select(_ => new ReactorConfig(
        RecvBufferSize: 32 * 1024,
        BufferRingEntries: 16 * 1024,
        IncrementalBufferConsumption: false  // set true for kernel 6.12+
    )).ToArray()
});
```

Key `ReactorConfig` options:

| Option | Default | Description |
|---|---|---|
| `RingEntries` | 8192 | io_uring SQ/CQ depth |
| `RecvBufferSize` | 32KB | Per-buffer size |
| `BufferRingEntries` | 16384 | Number of pre-allocated recv buffers |
| `BatchCqes` | 4096 | Max CQEs per loop iteration |
| `CqTimeout` | 1ms | Wait timeout (nanoseconds) |
| `IncrementalBufferConsumption` | false | Per-connection buffer rings (kernel 6.12+) |

## Architecture

One acceptor thread distributes connections round-robin to N reactor threads. Each reactor owns its own `io_uring` instance, buffer ring, and connection map. No locks on hot paths — all cross-thread coordination uses lock-free MPSC queues.

Key features: multishot accept/recv, provided buffer rings, `DEFER_TASKRUN`, `SINGLE_ISSUER`, optional `SQPOLL`, zero-allocation async via `IValueTaskSource`, connection pooling.

## Examples

```bash
dotnet run --project Examples -- raw          # zero-copy ring API
dotnet run --project Examples -- pipereader   # PipeReader adapter
dotnet run --project Examples -- stream       # Stream adapter
dotnet run --project Examples -- sqpoll       # SQPOLL mode
```

## Project Structure

```
core/           Shared library (utils, ConnectionBase, adapters)
zerg/           Main library (Engine, Reactor, native io_uring shim)
terraform/      Alternative pure-C# io_uring implementation (no native deps)
Examples/       Usage examples
Tests/          End-to-end tests
```

## License

MIT
