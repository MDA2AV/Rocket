---
title: Configuration
weight: 3
---

zerg is configured through three types: `EngineOptions` (top-level), `ReactorConfig` (per-reactor), and `AcceptorConfig` (acceptor thread). All have sensible defaults so you can start with just a port number.

## EngineOptions

The top-level configuration passed to `new Engine(options)`.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `ReactorCount` | `int` | `1` | Number of reactor threads to spawn. Each gets its own `io_uring` instance. |
| `Ip` | `string` | `"0.0.0.0"` | IP address to bind the listening socket to. Use `"::"` for IPv6. |
| `Port` | `ushort` | `8080` | TCP port to listen on. |
| `Backlog` | `int` | `65535` | Kernel listen backlog for pending connections. |
| `AcceptorConfig` | `AcceptorConfig` | `new()` | Configuration for the acceptor ring and event loop. |
| `ReactorConfigs` | `ReactorConfig[]` | `null` | Per-reactor configuration array. Auto-initialized with defaults if null. Must have at least `ReactorCount` entries if provided. |

### Example

```csharp
var engine = new Engine(new EngineOptions
{
    Ip = "0.0.0.0",
    Port = 8080,
    ReactorCount = 12,
    Backlog = 65535
});
```

## ReactorConfig

Configuration for a single reactor's `io_uring` instance and event loop. This is a `sealed record` -- all properties have defaults.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `RingFlags` | `uint` | `SINGLE_ISSUER \| DEFER_TASKRUN` | `io_uring` setup flags. See [Ring Flags](#ring-flags) below. |
| `SqCpuThread` | `int` | `-1` | CPU core to pin the SQPOLL kernel thread to. Only used with `IORING_SETUP_SQPOLL`. `-1` = kernel decides. |
| `SqThreadIdleMs` | `uint` | `100` | How long (ms) the SQPOLL kernel thread stays alive without submissions before sleeping. |
| `RingEntries` | `uint` | `8192` | SQ/CQ size. Upper bound on in-flight operations (recv, send, cancel) the reactor can have at once. |
| `RecvBufferSize` | `int` | `32768` (32 KB) | Size of each receive buffer in the buffer ring. Larger values reduce syscalls for large payloads. |
| `BufferRingEntries` | `int` | `16384` | Number of pre-allocated recv buffers. Must be a power of two. |
| `BatchCqes` | `int` | `4096` | Maximum CQEs processed per loop iteration. Larger improves throughput under load. |
| `MaxConnectionsPerReactor` | `int` | `8192` | Upper bound on concurrent connections. Should be <= `RingEntries`. |
| `CqTimeout` | `long` | `1_000_000` (1 ms) | Timeout in nanoseconds passed to `io_uring_wait_cqes()`. Lower = lower tail latency, higher CPU. |
| `IncrementalBufferConsumption` | `bool` | `false` | Enable `IOU_PBUF_RING_INC`. The kernel packs multiple recvs into a single buffer, reducing buffer ring pressure. **Requires kernel 6.12+.** |

### Example: Per-Reactor Configuration

```csharp
var engine = new Engine(new EngineOptions
{
    Port = 8080,
    ReactorCount = 4,
    ReactorConfigs = Enumerable.Range(0, 4).Select(_ => new ReactorConfig(
        RecvBufferSize: 64 * 1024,       // 64 KB recv buffers
        BufferRingEntries: 32 * 1024,     // 32K buffers per reactor
        CqTimeout: 500_000,              // 0.5 ms timeout
        IncrementalBufferConsumption: true // kernel 6.12+
    )).ToArray()
});
```

## AcceptorConfig

Configuration for the acceptor thread's `io_uring` instance. This is a `sealed record`.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `RingFlags` | `uint` | `0` | `io_uring` setup flags for the acceptor ring. SQPOLL is usually unnecessary here. |
| `SqCpuThread` | `int` | `-1` | CPU core for SQPOLL thread. |
| `SqThreadIdleMs` | `uint` | `100` | SQPOLL idle timeout in milliseconds. |
| `RingEntries` | `uint` | `8192` | SQ/CQ size. Bounds in-flight accept completions. |
| `BatchSqes` | `uint` | `4096` | Max accepts processed per loop iteration. |
| `CqTimeout` | `long` | `100_000_000` (100 ms) | Wait timeout in nanoseconds. Higher than reactor default since accepts are burst-driven. |
| `IPVersion` | `IPVersion` | `IPv6DualStack` | IP stack for the listening socket. |

### IPVersion Enum

| Value | Description |
|-------|-------------|
| `IPv4Only` | Creates `AF_INET` socket. Only IPv4 clients can connect. |
| `IPv6DualStack` | Creates `AF_INET6` socket with `IPV6_V6ONLY=0`. Accepts both IPv4 and IPv6 clients. IPv4 clients appear as `::ffff:a.b.c.d`. |

### Example: Custom Acceptor

```csharp
var engine = new Engine(new EngineOptions
{
    Port = 443,
    ReactorCount = 8,
    AcceptorConfig = new AcceptorConfig(
        RingEntries: 16384,
        CqTimeout: 50_000_000,           // 50 ms
        IPVersion: IPVersion.IPv4Only
    )
});
```

## Ring Flags

`io_uring` setup flags control how the kernel processes submissions and completions. You can combine flags with bitwise OR.

| Flag | Value | Description |
|------|-------|-------------|
| `IORING_SETUP_SQPOLL` | `1 << 1` | Kernel thread polls the SQ, eliminating submit syscalls. Trades a CPU core for lower latency. |
| `IORING_SETUP_SQ_AFF` | `1 << 2` | Pin the SQPOLL kernel thread to the CPU specified by `SqCpuThread`. |
| `IORING_SETUP_SINGLE_ISSUER` | `1 << 12` | Optimize for a single submitting thread. **Default for reactors.** |
| `IORING_SETUP_DEFER_TASKRUN` | `1 << 13` | Defer kernel task_work execution, reducing latency spikes. **Default for reactors.** |
| `IORING_SETUP_CQSIZE` | `1 << 3` | Allow CQ size different from SQ size. |
| `IORING_SETUP_CLAMP` | `1 << 4` | Clamp queue sizes to kernel-supported limits. |

### SQPOLL Example

```csharp
// Enable SQPOLL on reactor 0, pinned to CPU 2
var config = new ReactorConfig(
    RingFlags: ABI.IORING_SETUP_SQPOLL | ABI.IORING_SETUP_SQ_AFF | ABI.IORING_SETUP_SINGLE_ISSUER,
    SqCpuThread: 2,
    SqThreadIdleMs: 200
);
```

## Memory Budget

A rough estimate of per-reactor memory usage:

| Component | Formula | Default |
|-----------|---------|---------|
| Buffer ring | `BufferRingEntries * RecvBufferSize` | 16384 * 32 KB = **512 MB** |
| Write slabs | `MaxConnectionsPerReactor * 16 KB` | 8192 * 16 KB = **128 MB** |
| Ring entries | `RingEntries * sizeof(SQE/CQE)` | ~1 MB |

For a 4-reactor server with defaults, the buffer ring alone accounts for ~2 GB. Adjust `BufferRingEntries` and `RecvBufferSize` based on your workload and available memory.
