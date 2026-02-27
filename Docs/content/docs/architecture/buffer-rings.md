---
title: Buffer Rings
weight: 3
---

Buffer rings (also called "provided buffers") are a core `io_uring` feature that zerg uses for zero-copy receive operations. Instead of userspace providing a buffer with each recv call, the kernel picks from a pre-registered pool.

## How Buffer Rings Work

### Setup

When a reactor starts, it creates a buffer ring and populates it with pre-allocated buffers:

```
1. Allocate a contiguous slab of unmanaged memory (64-byte aligned)
2. Register the buffer ring with io_uring:
     br = shim_setup_buf_ring(ring, entries, bgid, 0, &ret)
3. Divide the slab into fixed-size chunks and add each to the ring:
     for i in 0..entries:
         shim_buf_ring_add(br, slab + i * bufSize, bufSize, i, mask, i)
4. Publish all buffers to the kernel:
     shim_buf_ring_advance(br, entries)
```

Each buffer is identified by a 16-bit `bufferId` (0 to `BufferRingEntries - 1`). The slab is a single contiguous allocation so the address of any buffer is `slab + bufferId * RecvBufferSize`.

### Receive

When multishot recv completes for a connection:

```
CQE arrives:
    cqe->res    = bytes received (or negative errno)
    cqe->flags  = IORING_CQE_F_BUFFER | (bufferId << 16)

Reactor extracts:
    bufferId = shim_cqe_buffer_id(cqe)
    ptr      = slab + bufferId * bufSize
    length   = cqe->res
```

The kernel has already written the received data into the selected buffer. The reactor creates a `RingItem(ptr, length, bufferId)` and enqueues it to the connection's receive ring.

### Consumption

The application handler drains `RingItem`s from the connection:

```csharp
while (connection.TryGetRing(result.TailSnapshot, out RingItem ring))
{
    ReadOnlySpan<byte> data = ring.AsSpan();  // zero-copy view
    // process data...
    connection.ReturnRing(ring.BufferId);      // return to kernel
}
```

### Return

When the handler calls `ReturnRing(bufferId)`, the buffer ID is enqueued to the reactor's return queue (an MPSC queue). On the next loop iteration, the reactor returns all queued buffers to the kernel:

```
while returnQ.TryDequeue(out bufferId):
    shim_buf_ring_add(br, slab + bufferId * bufSize, bufSize, bufferId, mask, idx++)
shim_buf_ring_advance(br, count)
```

## Incremental Buffer Consumption (Kernel 6.12+)

With `IncrementalBufferConsumption: true`, the kernel can partially consume a buffer across multiple recv CQEs instead of consuming one entire buffer per CQE. This is enabled by passing the `IOU_PBUF_RING_INC` flag when setting up the buffer ring.

### How It Works

Without incremental consumption, each recv CQE consumes an entire buffer — even a 50-byte read takes a full 32 KB buffer. With incremental consumption, the kernel writes successive recvs into the **same buffer at increasing offsets**:

```
Buffer #7 (32 KB):
  CQE 1: bytes 0–99     (100 B)   flags: IORING_CQE_F_BUF_MORE  ← kernel still owns it
  CQE 2: bytes 100–299  (200 B)   flags: IORING_CQE_F_BUF_MORE  ← kernel still owns it
  CQE 3: bytes 300–349  (50 B)    flags: (none)                  ← kernel is done
```

All three CQEs share the same `bufferId` but produce separate `RingItem`s with different `ptr` and `length` values. The user calls `ReturnRing()` for each one independently.

### Refcount Tracking

The reactor tracks three things per buffer (only when incremental is enabled):

| Tracking Array | Purpose |
|---------------|---------|
| `_bufferOffsets[bid]` | Where the next CQE's data starts within the buffer |
| `_bufferRefCounts[bid]` | How many outstanding `RingItem`s reference this buffer |
| `_bufferKernelDone[bid]` | Whether the final CQE (no `IORING_CQE_F_BUF_MORE`) has been seen |

A buffer is only returned to the kernel ring when **both** conditions are met:
- `refcount == 0` (all user `ReturnRing()` calls done)
- `kernelDone == true` (kernel won't produce more CQEs for this buffer)

### Transparent to User Code

The user-facing API is unchanged. `RingItem`, `ReturnRing()`, `ConnectionPipeReader`, and `ConnectionStream` all work identically. The refcounting is internal to the reactor thread — no atomics or locking needed.

### When It Helps

Incremental consumption reduces buffer ring pressure when:
- Many **small reads** relative to buffer size (e.g., small HTTP requests on 32 KB buffers)
- **TCP fragmentation** splits data into multiple segments received as separate CQEs
- **High connection count** with slow buffer returns, approaching buffer exhaustion

It has **no effect** on sequential request-response workloads where each recv consumes one buffer and returns it before the next request arrives.

## Buffer Lifecycle

```
                ┌──────────────────────────────────────────┐
                │              Buffer Ring                   │
                │  ┌─────┬─────┬─────┬─────┬─────┐         │
                │  │ B0  │ B1  │ B2  │ ... │ Bn  │         │
                │  └──┬──┴──┬──┴──┬──┴─────┴──┬──┘         │
                └─────┼─────┼─────┼───────────┼────────────┘
                      │     │     │           │
      ┌───────────────┘     │     │           └──────────────┐
      ▼                     ▼     ▼                          ▼
  ┌────────┐          ┌────────┐ ┌────────┐           ┌────────┐
  │ Kernel │          │ Kernel │ │  User  │           │  User  │
  │ (free) │          │ (recv) │ │ (proc) │           │ (done) │
  └────────┘          └───┬────┘ └───┬────┘           └───┬────┘
                          │          │                    │
                          │ CQE      │ TryGetRing()       │ ReturnRing()
                          ▼          ▼                    ▼
                    ┌──────────┐ ┌──────────┐      ┌──────────┐
                    │ RingItem │ │ Handler  │      │ Return Q │
                    │ enqueued │ │ draining │      │ → kernel │
                    └──────────┘ └──────────┘      └──────────┘
```

A buffer transitions through these states:

| State | Owner | Description |
|-------|-------|-------------|
| **Free** | Kernel | Available in the buffer ring for the next recv |
| **In-flight** | Kernel | Selected by kernel for an active recv operation |
| **Enqueued** | Reactor | Data received, `RingItem` pushed to connection's SPSC ring |
| **Processing** | Handler | Handler has dequeued the `RingItem` and is reading the data |
| **Returning** | Return queue | Handler called `ReturnRing()`, buffer ID queued for return |
| **Returned** | Kernel | Reactor added buffer back to the ring via `buf_ring_add` + `advance` |

## Configuration

| Config Property | Default | Impact |
|----------------|---------|--------|
| `BufferRingEntries` | 16384 | Total buffers per reactor. Must be power of two. More buffers = more concurrent in-flight receives. |
| `RecvBufferSize` | 32 KB | Size of each buffer. Larger = fewer buffers needed for big payloads, but more memory per buffer. |

**Memory formula:** `BufferRingEntries * RecvBufferSize` per reactor.

With defaults: 16384 * 32 KB = **512 MB per reactor**.

## Important Rules

1. **Always return buffers.** Every buffer obtained via `TryGetRing()` or `GetAllSnapshotRings()` must eventually be returned via `ReturnRing()` or `ReturnRingBuffers()`. Leaked buffers deplete the pool and eventually stall receives.

2. **Don't access buffer data after returning.** Once `ReturnRing(bufferId)` is called, the kernel may reuse that buffer immediately. Any pointer or span referencing the buffer becomes invalid.

3. **Return from the handler thread.** `ReturnRing()` enqueues to an MPSC queue that the reactor drains. It's safe to call from any thread, but typically called from the handler after processing.

4. **Buffer ring exhaustion.** If all buffers are in-flight or held by handlers, new recv operations will fail. The reactor handles this gracefully -- multishot recv CQEs may stop arriving until buffers are returned. If the connection's SPSC ring is full (1024 items), the connection is closed as a safety measure.
