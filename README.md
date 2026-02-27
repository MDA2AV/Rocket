[![NuGet](https://img.shields.io/nuget/v/zerg.svg)](https://www.nuget.org/packages/zerg/)

# zerg

**zerg** is an experimental, low-level TCP server framework built in C# on top of Linux `io_uring`. It intentionally avoids "magic" abstraction layers and gives the developer direct control over sockets, buffers, queues, and scheduling.

- **Author:** Diogo Martins
- **License:** MIT
- **Repository:** https://github.com/MDA2AV/zerg
- **NuGet:** https://www.nuget.org/packages/zerg/
- **Target Frameworks:** .NET 8.0, .NET 9.0, .NET 10.0

---

## Documentation

Full documentation is available at **https://mda2av.github.io/zerg/**

| Page | Description |
|---|---|
| [Getting Started](https://mda2av.github.io/zerg/docs/getting-started/installation/) | Installation, quick start, configuration |
| [Architecture](https://mda2av.github.io/zerg/docs/architecture/reactor-pattern/) | Reactor pattern, io_uring, threading model, connection lifecycle, buffer rings |
| [API Reference](https://mda2av.github.io/zerg/docs/api-reference/) | Engine, Connection Read/Write, ConnectionPipeReader, ConnectionStream, Configuration |
| [Guides](https://mda2av.github.io/zerg/docs/guides/zero-allocation/) | Zero-allocation patterns, buffer management, performance tuning |
| [Internals](https://mda2av.github.io/zerg/docs/internals/) | Memory management, native interop, MPSC/SPSC queues |

---

## Requirements

- **Linux** (kernel 6.1+ required for multishot accept/recv, buffer rings, `DEFER_TASKRUN`)
- **.NET 8.0**, **.NET 9.0**, or **.NET 10.0** SDK
- **liburing** (the native shim `liburingshim.so` is bundled in the NuGet package for `linux-x64` and `linux-musl-x64`)

---

## Installation

### Via NuGet

```bash
dotnet add package zerg
```

### From Source

```bash
git clone https://github.com/MDA2AV/zerg.git
cd zerg
dotnet build
```

### Publishing with AOT

```bash
dotnet publish -f net10.0 -c Release /p:PublishAot=true /p:OptimizationPreference=Speed
```

---

## Architecture Overview

zerg follows a **split architecture** with one acceptor thread and N reactor threads, each owning its own `io_uring` instance:

```
                          ┌─────────────────────────────────────────────┐
                          │              KERNEL SPACE                   │
                          │                                             │
    ┌────────┐     TCP    │     TCP/IP Stack ──► Listening Socket       │
    │Client 1│────────────│──────────────────────────►                  │
    │Client 2│────────────│──────────────────────────►                  │
    │Client 3│────────────│──────────────────────────►                  │
    │  ...   │────────────│──────────────────────────►                  │
    └────────┘            └──────────────┬────────────────────────────  │
                                         │                             │
             ┌───────────────────────────┼─────────────────────────────┘
             │                           │
             │       USER SPACE          ▼
             │       ┌───────────────────────────────────────┐
             │       │           ACCEPTOR THREAD             │
             │       │                                       │
             │       │  io_uring ◄── multishot accept        │
             │       │  (one SQE → CQE per new connection)   │
             │       │                                       │
             │       │  for each accepted fd:                │
             │       │    setsockopt(fd, TCP_NODELAY)        │
             │       │    enqueue to reactor[next++ % N]     │
             │       └───────┬──────────┬──────────┬─────────┘
             │               │          │          │
             │           lock-free  lock-free  lock-free
             │          ConcurrentQ ConcurrentQ ConcurrentQ
             │               │          │          │
             │               ▼          ▼          ▼
             │       ┌───────────┐ ┌───────────┐ ┌───────────┐
             │       │ REACTOR 0 │ │ REACTOR 1 │ │ REACTOR N │
             │       │           │ │           │ │           │
             │       │ io_uring  │ │ io_uring  │ │ io_uring  │
             │       │ buf_ring  │ │ buf_ring  │ │ buf_ring  │
             │       │ conn_map  │ │ conn_map  │ │ conn_map  │
             │       │ flush_Q   │ │ flush_Q   │ │ flush_Q   │
             │       │ return_Q  │ │ return_Q  │ │ return_Q  │
             │       │           │ │           │ │           │
             │       │ multishot │ │ multishot │ │ multishot │
             │       │ recv+send │ │ recv+send │ │ recv+send │
             │       └─────┬─────┘ └─────┬─────┘ └─────┬─────┘
             │             │             │             │
             │             └──────┬──────┘─────────────┘
             │                    ▼
             │       Channel<ConnectionItem>
             │                    │
             │                    ▼
             │          Engine.AcceptAsync()
             │                    │
             │                    ▼
             │          Application Handlers
             │       (ReadAsync ◄──► Write + FlushAsync)
             │
             └─────────────────────────────────────────────────────────
```

### Acceptor Thread
- Listens on a TCP socket and accepts new connections via `io_uring` multishot accept
- Distributes accepted connections to reactor threads in round-robin order

### Reactor Threads
Each reactor owns:
- Its own `io_uring` instance for recv/send operations
- A pre-allocated **buffer ring** for zero-copy receives
- A dictionary of active connections (fd → Connection)
- Lock-free MPSC queues for cross-thread coordination

### Key Design Principles
- **No thread contention:** Each connection belongs to exactly one reactor
- **Explicit buffer lifetimes:** Consumers must return buffers to the kernel after processing
- **Allocation-free hot paths:** Uses unmanaged memory, `ValueTask`, and object pooling
- **Multishot operations:** Single submission produces multiple completions

> See the full [Architecture docs](https://mda2av.github.io/zerg/docs/architecture/reactor-pattern/) for deep dives into the reactor pattern, threading model, connection lifecycle, and buffer rings.

---

## Quick Start

```csharp
using zerg.Engine;
using zerg.Engine.Configs;

var engine = new Engine(new EngineOptions
{
    Port = 8080,
    ReactorCount = 1
});
engine.Listen();

var cts = new CancellationTokenSource();

// Graceful shutdown on Enter key
_ = Task.Run(() => {
    Console.ReadLine();
    engine.Stop();
    cts.Cancel();
});

try
{
    while (engine.ServerRunning)
    {
        var connection = await engine.AcceptAsync(cts.Token);
        if (connection is null) continue;

        // Fire-and-forget connection handler
        _ = HandleConnectionAsync(connection);
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine("Server stopped.");
}
```

### Minimal Connection Handler

```csharp
static async Task HandleConnectionAsync(Connection connection)
{
    while (true)
    {
        var result = await connection.ReadAsync();
        if (result.IsClosed) break;

        // Get received buffers
        var rings = connection.GetAllSnapshotRingsAsUnmanagedMemory(result);

        // Process data...

        // Return buffers to the kernel
        rings.ReturnRingBuffers(connection.Reactor);

        // Write a response
        connection.Write("HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nOK"u8);
        await connection.FlushAsync();
        connection.ResetRead();
    }
}
```

---

## Configuration

### EngineOptions

| Property | Type | Default | Description |
|---|---|---|---|
| `ReactorCount` | `int` | `1` | Number of reactor threads to spawn |
| `Ip` | `string` | `"0.0.0.0"` | IP address to bind to |
| `Port` | `ushort` | `8080` | TCP port to listen on |
| `Backlog` | `int` | `65535` | Listen backlog for pending connections |
| `AcceptorConfig` | `AcceptorConfig` | `new()` | Acceptor thread configuration |
| `ReactorConfigs` | `ReactorConfig[]` | `null` | Per-reactor configurations (auto-filled if null) |

### ReactorConfig

| Property | Type | Default | Description |
|---|---|---|---|
| `RingFlags` | `uint` | `SINGLE_ISSUER \| DEFER_TASKRUN` | `io_uring` setup flags |
| `SqCpuThread` | `int` | `-1` | CPU affinity for SQPOLL thread (-1 = kernel decides) |
| `SqThreadIdleMs` | `uint` | `100` | SQPOLL idle timeout before sleeping |
| `RingEntries` | `uint` | `8192` | SQ/CQ size (max in-flight operations) |
| `RecvBufferSize` | `int` | `32768` | Size of each receive buffer in bytes |
| `BufferRingEntries` | `int` | `16384` | Number of pre-allocated recv buffers (must be power of 2) |
| `BatchCqes` | `int` | `4096` | Max CQEs processed per loop iteration |
| `MaxConnectionsPerReactor` | `int` | `8192` | Max concurrent connections per reactor |
| `CqTimeout` | `long` | `1000000` | Wait timeout in nanoseconds (1ms) |

### AcceptorConfig

| Property | Type | Default | Description |
|---|---|---|---|
| `RingFlags` | `uint` | `0` | `io_uring` setup flags |
| `SqCpuThread` | `int` | `-1` | CPU affinity for SQPOLL thread |
| `SqThreadIdleMs` | `uint` | `100` | SQPOLL idle timeout |
| `RingEntries` | `uint` | `8192` | SQ/CQ size |
| `BatchSqes` | `uint` | `4096` | Max accepts processed per loop iteration |
| `CqTimeout` | `long` | `100000000` | Wait timeout in nanoseconds (100ms) |
| `IPVersion` | `IPVersion` | `IPv6DualStack` | IPv4, IPv6, or IPv6DualStack |

### Multi-Reactor Configuration Example

```csharp
var engine = new Engine(new EngineOptions
{
    Port = 8080,
    ReactorCount = 12,
    ReactorConfigs = Enumerable.Range(0, 12).Select(_ => new ReactorConfig(
        RecvBufferSize: 64 * 1024,
        BufferRingEntries: 32 * 1024,
        CqTimeout: 500_000
    )).ToArray()
});
```

> Full config reference: [Configuration docs](https://mda2av.github.io/zerg/docs/api-reference/configuration/)

---

## Connection API

### Engine Lifecycle

```csharp
// Create and start
var engine = new Engine(options);
engine.Listen();

// Accept connections
Connection? conn = await engine.AcceptAsync(cancellationToken);

// Shutdown
engine.Stop();
```

### Connection Properties

| Property | Type | Description |
|---|---|---|
| `ClientFd` | `int` | The OS file descriptor for this connection |
| `Reactor` | `Engine.Reactor` | The reactor that owns this connection |

---

## Reading Data

zerg provides both high-level and low-level read APIs. The core contract is:

1. **Only one `ReadAsync()` can be outstanding per connection at a time**
2. After processing data, **return buffers** to the kernel via `ReturnRing()`
3. Call `ResetRead()` to signal readiness for the next read

```
    ┌──────────────────────────────────────────────────────────────────┐
    │                    READ LIFECYCLE                                │
    │                                                                  │
    │   await ReadAsync()                                              │
    │         │                                                        │
    │         ▼                                                        │
    │   ┌─────────────────┐     ┌─────────────────────────────────┐   │
    │   │  RingSnapshot     │     │  Option A: High-Level API       │   │
    │   │  .IsClosed       │────►│  GetAllSnapshotRingsAs          │   │
    │   │  .TailSnapshot   │     │  UnmanagedMemory(result)        │   │
    │   └─────────────────┘     │  .ToReadOnlySequence()          │   │
    │                            └──────────────┬──────────────────┘   │
    │   ┌─────────────────┐                     │                      │
    │   │  Option B:       │                     │                      │
    │   │  Low-Level API   │                     │                      │
    │   │  TryGetRing()    │                     │                      │
    │   │  ring.AsSpan()   │                     │                      │
    │   └────────┬────────┘                     │                      │
    │            │                               │                      │
    │            ▼                               ▼                      │
    │   ┌──────────────────────────────────────────────┐               │
    │   │  Return buffers to kernel                     │               │
    │   │  rings.ReturnRingBuffers(connection.Reactor)  │               │
    │   │  ── or ──                                     │               │
    │   │  connection.ReturnRing(ring.BufferId)         │               │
    │   └──────────────────────┬───────────────────────┘               │
    │                          │                                        │
    │                          ▼                                        │
    │               connection.ResetRead()                             │
    │                          │                                        │
    │                          ▼                                        │
    │                await ReadAsync()  ← loop                         │
    └──────────────────────────────────────────────────────────────────┘
```

### High-Level API

```csharp
// Wait for data
RingSnapshot result = await connection.ReadAsync();
if (result.IsClosed) return; // Connection was closed

// Get all received buffers as UnmanagedMemoryManager[]
var rings = connection.GetAllSnapshotRingsAsUnmanagedMemory(result);

// Create a ReadOnlySequence for easy slicing/parsing
ReadOnlySequence<byte> sequence = rings.ToReadOnlySequence();

// Return all buffers when done
rings.ReturnRingBuffers(connection.Reactor);

// Reset for next read
connection.ResetRead();
```

### Low-Level API

For fine-grained control, consume buffers one at a time:

```csharp
RingSnapshot result = await connection.ReadAsync();
if (result.IsClosed) return;

// Iterate through individual ring buffers
while (connection.TryGetRing(result.TailSnapshot, out RingItem ring))
{
    ReadOnlySpan<byte> data = ring.AsSpan();
    // Process data...
    connection.ReturnRing(ring.BufferId);
}

connection.ResetRead();
```

### RingSnapshot

| Property | Type | Description |
|---|---|---|
| `TailSnapshot` | `long` | Snapshot of the receive ring tail at read time |
| `IsClosed` | `bool` | Whether the connection was closed |

### RingItem

| Property | Type | Description |
|---|---|---|
| `Ptr` | `byte*` | Pointer to the receive buffer |
| `Length` | `int` | Number of bytes received |
| `BufferId` | `ushort` | Kernel buffer ID (used with `ReturnRing()`) |

### Adapter APIs

For convenience, zerg provides two adapter classes that wrap the low-level ring API:

#### ConnectionPipeReader (Zero-Copy)

```csharp
var reader = new ConnectionPipeReader(connection);

while (true)
{
    var result = await reader.ReadAsync();
    if (result.IsCompleted) break;

    var buffer = result.Buffer;
    // Parse buffer...

    reader.AdvanceTo(consumed, examined);
}

reader.Complete();
```

Kernel buffers stay held until `AdvanceTo` releases them — no copies. Supports partial consumption for protocol parsing.

#### ConnectionStream (BCL Compatibility)

```csharp
await using var stream = new ConnectionStream(connection);
var buf = new byte[4096];

while ((int n = await stream.ReadAsync(buf)) > 0)
{
    // Process buf[..n]
    await stream.WriteAsync(responseBytes);
    await stream.FlushAsync();
}
```

One copy per read. Use when integrating with APIs that require `Stream`.

> Full API reference: [Connection Read](https://mda2av.github.io/zerg/docs/api-reference/connection-read/), [ConnectionPipeReader](https://mda2av.github.io/zerg/docs/api-reference/connection-pipereader/), [ConnectionStream](https://mda2av.github.io/zerg/docs/api-reference/connection-stream/)

---

## Writing Data

```
    ┌──────────────────────────────────────────────────────────────────┐
    │                    WRITE LIFECYCLE                               │
    │                                                                  │
    │   connection.Write(data)          connection.GetSpan(size)       │
    │   ── or ──                        int written = Format(span);   │
    │   connection.Write(span)          connection.Advance(written);  │
    │         │                                   │                    │
    │         └──────────────┬────────────────────┘                    │
    │                        ▼                                         │
    │              Staged in write slab                                │
    │              (NativeMemory, no GC)                               │
    │                        │                                         │
    │                        ▼                                         │
    │            await connection.FlushAsync()                         │
    │                        │                                         │
    │                        ▼                                         │
    │              Reactor submits send SQE                            │
    │              (handles partial sends)                             │
    │                        │                                         │
    │                        ▼                                         │
    │              Kernel delivers to client                           │
    └──────────────────────────────────────────────────────────────────┘
```

### Simple Write (copies data to internal buffer)

```csharp
connection.Write("HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nOK"u8);
await connection.FlushAsync();
```

### IBufferWriter Interface

```csharp
Span<byte> span = connection.GetSpan(256);
// Write directly into the span...
int bytesWritten = FormatResponse(span);
connection.Advance(bytesWritten);
await connection.FlushAsync();
```

### Write/Flush Lifecycle

1. **Write:** Data is staged in the connection's write buffer
2. **FlushAsync:** Signals the reactor to issue a `send` SQE to the kernel
3. The reactor handles partial sends automatically (resubmits remaining data)
4. The write buffer is reset after the full send completes

> Full API reference: [Connection Write](https://mda2av.github.io/zerg/docs/api-reference/connection-write/)

---

## Examples

The repository includes example connection handlers demonstrating different API levels:

### Zero-Copy (Raw Ring API)

#### `Rings_as_ReadOnlySpan`

Simplest approach. Gets all snapshot rings and processes them as spans. Good starting point for understanding the API.

```
Examples/ZeroAlloc/Basic/Rings_as_ReadOnlySpan.cs
```

#### `Rings_as_ReadOnlySequence`

Same as above but creates a `ReadOnlySequence<byte>` from the rings, which is useful for `SequenceReader<byte>` based parsing.

```
Examples/ZeroAlloc/Basic/Rings_as_ReadOnlySequence.cs
```

### PipeReader Adapter

#### `PipeReaderExample`

Zero-copy reads via `ConnectionPipeReader`. Data stays in io_uring kernel buffers until explicitly consumed via `AdvanceTo`. Supports partial consumption for protocol parsing.

```
Examples/PipeReader/PipeReaderExample.cs
```

### Stream Adapter

#### `StreamExample`

BCL `Stream` compatibility via `ConnectionStream`. Copies received bytes into a managed buffer on each read. Use when integrating with APIs that require `Stream`.

```
Examples/Stream/StreamExample.cs
```

### Running Examples

```bash
# Default (PipeReader)
dotnet run --project Examples

# Specific handler
dotnet run --project Examples -- raw
dotnet run --project Examples -- pipereader
dotnet run --project Examples -- stream
```

---

## io_uring Primer

`io_uring` is a Linux kernel interface for asynchronous I/O based on shared-memory ring buffers:

```
    ┌──────────────────────────────────────────────────────────────────┐
    │  USERSPACE                                                       │
    │                                                                  │
    │   Your Code                            Your Handler              │
    │   ┌────────────────┐                   ┌────────────────┐       │
    │   │ prep_recv()    │                   │ process CQEs   │       │
    │   │ prep_send()    │                   │ dispatch by     │       │
    │   │ prep_accept()  │                   │ user_data tag   │       │
    │   └───────┬────────┘                   └───────▲────────┘       │
    │           │ write SQEs                         │ read CQEs      │
    │           ▼                                    │                 │
    │   ┌──────────────────────────────────────────────────────┐      │
    │   │              SHARED MEMORY (mmap'd)                   │      │
    │   │                                                       │      │
    │   │  ┌─────────────────────┐   ┌──────────────────────┐  │      │
    │   │  │  Submission Queue   │   │  Completion Queue     │  │      │
    │   │  │  [SQE][SQE][SQE].. │   │  [CQE][CQE]..        │  │      │
    │   │  └─────────────────────┘   └──────────────────────┘  │      │
    │   └──────────────────────────────────────────────────────┘      │
    │           │ kernel reads SQ            ▲ kernel writes CQ       │
    ├───────────┼────────────────────────────┼─────────────────────────┤
    │  KERNEL   ▼                            │                         │
    │       ┌──────────────────────────────────┐                      │
    │       │        I/O Processing            │                      │
    │       │     accept / recv / send          │                      │
    │       └──────────────────────────────────┘                      │
    └──────────────────────────────────────────────────────────────────┘

    SQE: [opcode][fd][buf/len][user_data][flags]    ← what to do
    CQE: [user_data][res][flags]                    ← what happened
```

### Features Used by zerg

| Feature | Description |
|---|---|
| **Multishot Accept** | Single submission produces a CQE for every new connection |
| **Multishot Recv** | Single submission per connection; kernel fills a buffer from the buffer ring for each packet |
| **Buffer Selection** | Pre-registered buffer pool; kernel picks a buffer and returns its ID in the CQE |
| **SQPOLL** (optional) | Kernel thread polls the SQ, eliminating the submit syscall at the cost of a dedicated CPU core |
| **DEFER_TASKRUN** | Defers kernel task execution for better async/await integration |
| **SINGLE_ISSUER** | Optimizes for single-thread submission (matches reactor model) |

---

## Performance Tuning

### Recv Buffer Configuration

| Tunable | Increase for... | Decrease for... |
|---|---|---|
| `RecvBufferSize` | Large payloads (fewer syscalls) | Low memory usage, small messages |
| `BufferRingEntries` | Many concurrent connections | Lower memory footprint |

### CQE Batching

| Tunable | Higher value | Lower value |
|---|---|---|
| `BatchCqes` | Better throughput under load | Lower per-loop latency |

### Timeout

| Tunable | Lower value (e.g. 1ms) | Higher value (e.g. 100ms) |
|---|---|---|
| `CqTimeout` | Lower tail latency, higher CPU | Lower CPU usage, higher tail latency |

### Ring Flags

| Flag | Effect |
|---|---|
| `IORING_SETUP_SQPOLL` | Kernel thread polls SQ; saves syscalls but dedicates a CPU core |
| `IORING_SETUP_DEFER_TASKRUN` | Better for async/await integration (default) |
| `IORING_SETUP_SQ_AFF` | Pin SQPOLL kernel thread to a specific CPU core |
| `IORING_SETUP_SINGLE_ISSUER` | Optimize for single-thread submission (default) |

> See [Performance Tuning](https://mda2av.github.io/zerg/docs/guides/performance-tuning/) and [Buffer Management](https://mda2av.github.io/zerg/docs/guides/buffer-management/) guides for more.

---

## Project Structure

```
zerg/                                         # Core library (NuGet package)
├── zerg.csproj
├── ABI/                                      # Linux system ABI bindings
│   ├── CPU.cs                                # CPU affinity (sched_setaffinity)
│   ├── Kernel.cs                             # Kernel-level utilities
│   ├── LinuxSocket.cs                        # Socket syscall wrappers
│   └── URing.cs                              # io_uring P/Invoke bindings (liburingshim)
├── Connection/                               # Per-connection state and APIs
│   ├── Connection.cs                         # Core connection class
│   ├── Connection.Read.cs                    # Read state, IValueTaskSource, async signaling
│   ├── Connection.Read.HighLevelApi.cs       # Batch read APIs (GetAllSnapshotRings, etc.)
│   ├── Connection.Read.LowLevelApi.cs        # Low-level streaming APIs (TryGetRing)
│   ├── Connection.Write.cs                   # Write buffer state
│   ├── Connection.Write.HighLevelApi.cs      # Write + FlushAsync
│   ├── Connection.Write.IBufferWriter.cs     # IBufferWriter<byte> implementation
│   ├── Connection.Write.LowLevelApi.cs       # Low-level write APIs
│   ├── ConnectionStream.cs                   # BCL Stream adapter
│   └── ConnectionPipeReader.cs               # PipeReader adapter
├── Engine/                                   # Reactor pattern implementation
│   ├── Engine.cs                             # Main coordinator
│   ├── Engine.Config.cs                      # Configuration and thread setup
│   ├── Engine.Acceptor.cs                    # Accept event loop
│   ├── Engine.Acceptor.Listener.cs           # Listener socket setup
│   ├── Engine.Reactor.cs                     # Reactor state and setup
│   ├── Engine.Reactor.Handle.cs              # CQE dispatch (recv/send/cancel)
│   ├── Engine.Reactor.HandleSubmitAndWaitCqe.cs       # Two-call submit pattern
│   ├── Engine.Reactor.HandleSubmitAndWaitSingleCall.cs # Single-call submit pattern
│   └── Configs/
│       ├── EngineOptions.cs                  # Top-level engine configuration
│       ├── ReactorConfig.cs                  # Per-reactor configuration
│       ├── AcceptorConfig.cs                 # Acceptor configuration
│       └── IPVersion.cs                      # IPv4 / IPv6 / DualStack enum
├── Utils/                                    # Data structures and helpers
│   ├── RingItem.cs                           # Received buffer metadata (ptr, len, buf_id)
│   ├── ReadResult.cs                         # RingSnapshot struct (read snapshot result)
│   ├── RingSegment.cs                        # ReadOnlySequence segment node
│   ├── WriteItem.cs                          # Write buffer descriptor
│   ├── PinnedByteSequence.cs                 # Pinned byte[] as ReadOnlySequence
│   ├── Memory/
│   │   └── MemoryExtensions.cs               # Memory helper extensions
│   ├── ReadOnlySpan/
│   │   └── ReadOnlySpanExtensions.cs         # Span parsing helpers
│   ├── UnmanagedMemoryManager/
│   │   ├── UnmanagedMemoryManager.cs         # Wraps unmanaged ptr as MemoryManager<byte>
│   │   └── UnmanagedMemoryManagerExtensions.cs  # Batch ring → sequence helpers
│   ├── SingleProducerSingleConsumer/
│   │   └── SpscRecvRing.cs                   # Lock-free SPSC ring buffer
│   └── MultiProducerSingleConsumer/
│       ├── MpscIntQueue.cs                   # Lock-free MPSC int queue
│       ├── MpscUShortQueue.cs                # Lock-free MPSC ushort queue (buffer returns)
│       ├── MpscRecvRing.cs                   # MPSC recv ring (reactor → connection)
│       └── MpscWriteItem.cs                  # MPSC write item queue
└── native/                                   # Bundled native libraries
    ├── uringshim.c                           # C shim source (wraps liburing)
    ├── uringshim.h                           # C shim header
    ├── liburingshim.so                       # Compiled shared library
    ├── linux-x64/liburingshim.so             # NuGet runtime: glibc
    └── linux-musl-x64/liburingshim.so        # NuGet runtime: musl (Alpine)
```

### Dependencies

| Dependency | Version | Purpose |
|---|---|---|
| `Microsoft.Extensions.ObjectPool` | 10.0.2 | Connection object pooling |
| `System.IO.Pipelines` | 9.0.4 | `PipeReader` adapter (`ConnectionPipeReader`) |
| `liburingshim.so` | bundled | C shim bridging P/Invoke to liburing |

---

## Threading Model

```
    ┌──────────────────────────────────────────────────────────────────┐
    │                                                                  │
    │   ACCEPTOR THREAD                                                │
    │   ┌─────────────────────────────────────────────────────┐       │
    │   │  io_uring: multishot accept                          │       │
    │   │  Accepts connections, sets TCP_NODELAY               │       │
    │   │  Distributes FDs round-robin to reactors             │       │
    │   └──────────┬──────────────┬──────────────┬────────────┘       │
    │              │              │              │                      │
    │        ConcurrentQ    ConcurrentQ    ConcurrentQ                │
    │        (lock-free)    (lock-free)    (lock-free)                 │
    │              │              │              │                      │
    │              ▼              ▼              ▼                      │
    │   ┌──────────────┐  ┌──────────────┐  ┌──────────────┐         │
    │   │  REACTOR 0   │  │  REACTOR 1   │  │  REACTOR N   │         │
    │   │              │  │              │  │              │         │
    │   │  Event Loop: │  │  Event Loop: │  │  Event Loop: │         │
    │   │  1. Drain     │  │  1. Drain     │  │  1. Drain     │         │
    │   │     new FDs   │  │     new FDs   │  │     new FDs   │         │
    │   │  2. Drain     │  │  2. Drain     │  │  2. Drain     │         │
    │   │     buf rets  │  │     buf rets  │  │     buf rets  │         │
    │   │  3. Drain     │  │  3. Drain     │  │  3. Drain     │         │
    │   │     flushes   │  │     flushes   │  │     flushes   │         │
    │   │  4. Process   │  │  4. Process   │  │  4. Process   │         │
    │   │     CQEs      │  │     CQEs      │  │     CQEs      │         │
    │   └──────┬───────┘  └──────┬───────┘  └──────┬───────┘         │
    │          │                 │                 │                    │
    │          ▼                 ▼                 ▼                    │
    │   ┌──────────────┐  ┌──────────────┐  ┌──────────────┐         │
    │   │  Handler     │  │  Handler     │  │  Handler     │         │
    │   │  Tasks       │  │  Tasks       │  │  Tasks       │         │
    │   │  (async/     │  │  (async/     │  │  (async/     │         │
    │   │   await)     │  │   await)     │  │   await)     │         │
    │   └──────────────┘  └──────────────┘  └──────────────┘         │
    │                                                                  │
    └──────────────────────────────────────────────────────────────────┘
```

**Thread safety guarantees:**
- Each connection belongs to exactly one reactor (no cross-thread contention)
- MPSC queues handle all cross-thread communication (lock-free)
- `Volatile.Read`/`Volatile.Write` and `Interlocked` operations enforce correct memory ordering
- Connection pooling uses generation counters to prevent stale access after reuse

---

## License

MIT License - Copyright (c) 2026 Diogo Martins (MDA2AV)
