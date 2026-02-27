---
title: Performance Tuning
weight: 4
---

This guide covers the key tunables in zerg and how to adjust them for your workload.

## Reactor Count

```csharp
ReactorCount = Environment.ProcessorCount
```

**Rule of thumb:** Start with one reactor per CPU core. Each reactor runs a tight event loop on a dedicated thread, so more reactors than cores leads to context switching overhead.

For mixed workloads (some connections do async I/O like database calls), you may benefit from slightly more reactors than cores since handler tasks yield to the thread pool during awaits.

## CQ Timeout

```csharp
CqTimeout = 1_000_000  // nanoseconds (1 ms)
```

The CQ timeout controls how long the reactor sleeps when no completions are available.

| Value | Tail Latency | CPU Usage |
|-------|-------------|-----------|
| `100_000` (0.1 ms) | Very low | High (more frequent wakeups) |
| `1_000_000` (1 ms) | Low | Moderate (default) |
| `10_000_000` (10 ms) | Moderate | Low |
| `100_000_000` (100 ms) | High | Very low |

For latency-sensitive servers, use 0.1-1 ms. For background or batch-oriented servers, 10-100 ms is fine.

The acceptor uses 100 ms by default since accept bursts are infrequent.

## Buffer Ring Sizing

### RecvBufferSize

```csharp
RecvBufferSize = 32 * 1024  // 32 KB per buffer
```

Each kernel recv writes into one of these buffers. If a recv delivers more data than the buffer size, the kernel fills the buffer and the remainder arrives in the next CQE.

| Workload | Recommended Size |
|----------|-----------------|
| Small messages (HTTP/1.1 requests) | 4-8 KB |
| Mixed traffic | 16-32 KB (default) |
| Large uploads/downloads | 64-128 KB |
| Websockets with large frames | 64+ KB |

### BufferRingEntries

```csharp
BufferRingEntries = 16 * 1024  // 16384 buffers
```

Total receive buffers available to the kernel per reactor. Must be a power of two.

**Size it based on:**
- `MaxConnectionsPerReactor` -- at minimum, one buffer per active connection
- Data holding time -- if handlers hold buffers during async work, you need more
- Burst capacity -- buffers to absorb data bursts without stalling

**Memory impact:** `BufferRingEntries * RecvBufferSize` per reactor.

## Ring Entries

```csharp
RingEntries = 8192
```

The SQ/CQ size. This is the maximum number of in-flight I/O operations:
- One multishot recv per active connection
- One send per flushing connection
- Cancel operations

Should be >= `MaxConnectionsPerReactor` to avoid running out of SQE slots.

## Batch CQEs

```csharp
BatchCqes = 4096
```

Maximum CQEs processed per loop iteration. Larger values improve throughput under load by amortizing loop overhead, but increase per-loop latency (time to service all CQEs before sleeping again).

For latency-sensitive applications, consider reducing this to 256-1024.

## SQPOLL

SQPOLL mode creates a kernel thread that continuously polls the submission queue:

```csharp
new ReactorConfig(
    RingFlags: ABI.IORING_SETUP_SQPOLL | ABI.IORING_SETUP_SQ_AFF | ABI.IORING_SETUP_SINGLE_ISSUER,
    SqCpuThread: 4,        // pin to CPU 4
    SqThreadIdleMs: 200     // sleep after 200ms idle
)
```

**Benefits:**
- Eliminates the `io_uring_enter()` syscall for submissions
- Reduced per-submission latency

**Costs:**
- Dedicates one CPU core per reactor
- Increased power consumption
- Requires `CAP_SYS_NICE` or appropriate permissions in containers

**When to enable:**
- You have spare cores (total cores > 2 * reactor count)
- Submission latency is your bottleneck
- You're already saturating network bandwidth

**When to avoid:**
- Core-constrained environments (containers, small VMs)
- Acceptor ring (multishot accept generates CQEs from interrupts, not submissions)

## DEFER_TASKRUN

```csharp
RingFlags = ABI.IORING_SETUP_SINGLE_ISSUER | ABI.IORING_SETUP_DEFER_TASKRUN
```

This is the **default** for reactors. It tells the kernel to defer task_work (completion callbacks) until the next `io_uring_enter()` call, rather than running them in interrupt context.

**Benefits:**
- Completions arrive at predictable points in the reactor loop
- Reduces latency spikes from interrupt-context work
- Better cache behavior

**When to disable:** Rarely. Only if you're seeing issues with specific kernel versions.

## Incremental Buffer Consumption

```csharp
IncrementalBufferConsumption = true  // requires kernel 6.12+
```

When enabled, the kernel packs multiple recvs into a single provided buffer at successive offsets instead of consuming one entire buffer per recv. This reduces buffer ring pressure.

**When to enable:**
- Many connections doing small reads (e.g., HTTP/1.1 with small request bodies)
- TCP fragmentation producing multiple CQEs per message
- Approaching buffer ring exhaustion under high connection count

**When it won't help:**
- Sequential request-response workloads (one recv per request, buffer returned before next)
- Large payloads that fill most of the buffer anyway
- Plenty of headroom in the buffer ring (default 16K buffers)

**Note:** If your kernel is older than 6.12, the flag is silently ignored and the reactor falls back to standard one-buffer-per-recv behavior.

## Connection Limits

```csharp
MaxConnectionsPerReactor = 8192
```

Upper bound on concurrent connections per reactor. This is a logical limit, not a hard allocation.

**Scaling formula:**
- Total concurrent connections = `ReactorCount * MaxConnectionsPerReactor`
- With defaults: 1 * 8192 = 8,192 connections
- With 12 reactors: 12 * 8192 = 98,304 connections

Ensure `MaxConnectionsPerReactor <= RingEntries` to avoid SQE exhaustion.

## Listen Backlog

```csharp
Backlog = 65535
```

Kernel queue for pending connections (accepted by kernel but not yet accepted by userspace). 65535 is the Linux maximum. Reduce only if you want to reject connections under load.

## Benchmarking Tips

1. **Warm up** -- run at least 10 seconds of load before measuring
2. **Pin cores** -- use CPU affinity to prevent migration
3. **Disable turbo boost** -- for consistent results, disable CPU frequency scaling
4. **Use wrk or h2load** -- standard HTTP benchmarking tools
5. **Watch for kernel limits** -- check `ulimit -n` (file descriptor limit) and `net.core.somaxconn`
6. **Profile with perf** -- `perf top` shows where CPU time is spent

### System Tuning

```bash
# Increase file descriptor limit
ulimit -n 1000000

# Increase somaxconn (listen backlog limit)
sysctl -w net.core.somaxconn=65535

# Increase local port range (for clients)
sysctl -w net.ipv4.ip_local_port_range="1024 65535"

# Disable TCP timestamps (minor latency improvement)
sysctl -w net.ipv4.tcp_timestamps=0
```

## Configuration Matrix

| Scenario | ReactorCount | RecvBufferSize | BufferRingEntries | CqTimeout |
|----------|-------------|---------------|-------------------|-----------|
| Low-latency API | CPU count | 4 KB | 8192 | 100,000 ns |
| HTTP server | CPU count | 32 KB | 16384 | 1,000,000 ns |
| Proxy/gateway | CPU count | 64 KB | 32768 | 500,000 ns |
| File transfer | CPU count / 2 | 128 KB | 4096 | 10,000,000 ns |
| IoT/many connections | CPU count | 2 KB | 65536 | 1,000,000 ns |
