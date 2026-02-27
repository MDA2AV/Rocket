---
title: Buffer Management
weight: 3
---

Correct buffer management is critical in zerg. Kernel-provided buffers are a finite pool -- leaking them will stall your server. This guide covers the buffer lifecycle, ownership rules, and common pitfalls.

## Buffer Ownership Model

Every buffer in zerg has exactly one owner at any time:

```
Kernel (buffer ring) → Reactor (CQE) → Connection (SPSC ring) → Handler → Return → Kernel
```

| State | Owner | What You Can Do |
|-------|-------|-----------------|
| In buffer ring | Kernel | Nothing -- kernel picks buffers for recv |
| CQE delivered | Reactor | Reactor creates `RingItem`, enqueues to connection |
| In SPSC ring | Connection | Waiting to be drained by handler |
| Dequeued | Handler | Read `Ptr`/`AsSpan()`. Must return when done. |
| Returned | Kernel | Buffer back in pool. All pointers are invalid. |

## The Golden Rule

**Every buffer obtained from the connection must be returned exactly once.**

```csharp
// Correct: obtain and return
while (connection.TryGetRing(result.TailSnapshot, out RingItem ring))
{
    ProcessData(ring.AsSpan());
    connection.ReturnRing(ring.BufferId);  // MUST call this
}
```

```csharp
// Also correct: batch obtain and return
var rings = connection.GetAllSnapshotRingsAsUnmanagedMemory(result);
// ... process ...
rings.ReturnRingBuffers(connection.Reactor);  // returns all at once
```

## Common Mistakes

### Forgetting to Return on Error

```csharp
// BAD: buffer leaked on exception
while (connection.TryGetRing(result.TailSnapshot, out RingItem ring))
{
    ProcessData(ring.AsSpan());  // throws!
    connection.ReturnRing(ring.BufferId);  // never reached
}

// GOOD: return in finally
while (connection.TryGetRing(result.TailSnapshot, out RingItem ring))
{
    try
    {
        ProcessData(ring.AsSpan());
    }
    finally
    {
        connection.ReturnRing(ring.BufferId);
    }
}
```

### Using a Buffer After Return

```csharp
// BAD: use-after-return
connection.TryGetRing(result.TailSnapshot, out RingItem ring);
connection.ReturnRing(ring.BufferId);
var data = ring.AsSpan();  // UNSAFE: kernel may have reused the buffer

// GOOD: copy first if needed
connection.TryGetRing(result.TailSnapshot, out RingItem ring);
byte[] copy = ring.AsSpan().ToArray();  // copy to managed memory
connection.ReturnRing(ring.BufferId);
ProcessLater(copy);  // safe: data is in managed memory
```

### Holding Buffers Too Long

```csharp
// BAD: holding buffer during async operation
while (connection.TryGetRing(result.TailSnapshot, out RingItem ring))
{
    await SomeDatabaseCallAsync(ring.AsSpan());  // buffer held during I/O
    connection.ReturnRing(ring.BufferId);
}

// GOOD: copy what you need, return immediately
while (connection.TryGetRing(result.TailSnapshot, out RingItem ring))
{
    var requestData = ring.AsSpan().ToArray();  // or parse inline
    connection.ReturnRing(ring.BufferId);  // return immediately
    await SomeDatabaseCallAsync(requestData);
}
```

## Return Path Internals

When you call `connection.ReturnRing(bufferId)`:

1. The `ushort` buffer ID is enqueued to the reactor's MPSC return queue
2. On the reactor's next loop iteration, it drains the return queue
3. For each returned buffer ID:
   ```c
   shim_buf_ring_add(br, slab + bufferId * bufSize, bufSize, bufferId, mask, idx)
   ```
4. After processing all returns: `shim_buf_ring_advance(br, count)`
5. The kernel can now use these buffers for future recv operations

The return is **not immediate** -- there's a small delay (one reactor loop iteration) before the kernel can reuse the buffer. This is safe because the buffer ring has thousands of buffers.

### With Incremental Buffer Consumption

When `IncrementalBufferConsumption` is enabled (kernel 6.12+), multiple `RingItem`s can share the same `bufferId` at different offsets. The reactor uses internal refcounting so you don't need to change anything:

1. Each `ReturnRing()` call decrements the buffer's refcount
2. The buffer is only returned to the kernel when `refcount == 0` **and** the kernel is done writing to it
3. All tracking is internal to the reactor thread — the `ReturnRing()` API is identical

## Buffer Pool Exhaustion

If all buffers are in-flight (held by the kernel or handlers):

- New recv operations have no buffers to use
- Multishot recv CQEs may stop arriving for affected connections
- If the connection's SPSC ring fills (1024 items), the connection is **force-closed**

### Monitoring

```csharp
// Per-connection ring fill level
long pending = connection.TotalRingCount;

// After ReadAsync
int batchSize = connection.SnapshotRingCount;
```

### Prevention

1. **Return buffers promptly** -- don't hold them across awaits
2. **Size `BufferRingEntries` appropriately** -- more buffers = more headroom
3. **Process data inline** -- parse and respond in the same sync block when possible
4. **Copy if you need to hold data** -- copy to managed/unmanaged memory, return the buffer

## Write Buffer Management

The write path uses a separate unmanaged slab per connection (default 16 KB):

```
Write(data) → copies into slab → FlushAsync() → reactor sends → slab reset
```

The write slab is automatically reset after `FlushAsync()` completes. You don't need to manage it manually.

**Size limit:** The total staged data (all `Write()` calls between flushes) must fit in the slab. If your responses are larger than 16 KB, increase the slab size in the `Connection` constructor.

**Flush barrier:** While a flush is in progress (`_flushInProgress == 1`), all `Write()` calls throw `InvalidOperationException`. Wait for the flush to complete before writing again.

## Memory Layout Summary

```
Per-Reactor:
  Buffer Ring Slab: BufferRingEntries * RecvBufferSize bytes (unmanaged, aligned)
  Buffer Ring:      BufferRingEntries * sizeof(entry)        (kernel-managed)

Per-Connection:
  Write Slab:       16 KB default (unmanaged, 64-byte aligned)
  SPSC Recv Ring:   1024 * sizeof(RingItem)                  (managed array)
```
