---
title: Memory Management
weight: 4
---

zerg minimizes GC pressure by using unmanaged memory for all hot-path data: receive buffers, write slabs, and inflight buffers. This page describes the memory management strategy.

## Unmanaged Allocations

All performance-critical buffers are allocated outside the managed heap:

| Component | Size | Alignment | Lifetime |
|-----------|------|-----------|----------|
| Buffer ring slab (shared mode) | `BufferRingEntries * RecvBufferSize` per reactor | 64 bytes | Reactor lifetime |
| Buffer ring slab (incremental mode) | `ConnectionBufferRingEntries * RecvBufferSize` per connection | 64 bytes | Connection pool lifetime (survives reuse) |
| Write slab | 16 KB per connection (configurable) | 64 bytes | Connection lifetime |
| Inflight buffer | User-defined (typically 16 KB) per handler | 64 bytes | Handler lifetime |

### Why 64-Byte Alignment?

64 bytes is a common L1 cache line size on x86_64. Aligned allocations prevent false sharing and ensure optimal memory access patterns for DMA operations used by the kernel.

```csharp
byte* ptr = (byte*)NativeMemory.AlignedAlloc((nuint)size, 64);
// ...
NativeMemory.AlignedFree(ptr);
```

## UnmanagedMemoryManager

The `UnmanagedMemoryManager` class bridges unmanaged pointers and .NET's managed memory model:

```csharp
public sealed unsafe class UnmanagedMemoryManager : MemoryManager<byte>
{
    private byte* _ptr;
    private int _length;
    private ushort BufferId;
    private bool _freeable;

    public Span<byte> GetSpan() => new Span<byte>(_ptr, _length);
    public MemoryHandle Pin(int elementIndex = 0) => new MemoryHandle(_ptr + elementIndex);
    public void Unpin() { }  // no-op: already unmanaged
    public void Free() { if (_freeable) NativeMemory.AlignedFree(_ptr); }
}
```

This enables zero-copy interop between kernel buffers and .NET APIs that accept `Memory<byte>`, `ReadOnlyMemory<byte>`, or `ReadOnlySequence<byte>`.

### Constructors

| Constructor | Freeable | Use Case |
|-------------|----------|----------|
| `(byte* ptr, int length)` | Yes | Owned unmanaged allocations |
| `(byte* ptr, int length, bool freeable)` | Configurable | Borrowed pointers (e.g., buffer ring) |
| `(byte* ptr, int length, ushort bufferId)` | Yes | Recv buffers with buffer ring ID |
| `(byte* ptr, int length, ushort bufferId, bool freeable)` | Configurable | Full control |

For buffer ring receive data, `freeable` is typically `false` because the buffer belongs to the reactor's slab and is returned, not freed.

### Usage Pattern

```csharp
// Wrap kernel buffer in managed view
var manager = new UnmanagedMemoryManager(ring.Ptr, ring.Length, ring.BufferId, freeable: false);
ReadOnlyMemory<byte> memory = manager.Memory;  // zero allocation
```

## Buffer Ring Slab

In **shared mode** (default), each reactor pre-allocates a single contiguous slab for receive buffers. In **incremental mode** (`IncrementalBufferConsumption: true`), each connection owns its own slab of `ConnectionBufferRingEntries * RecvBufferSize` bytes, allocated on first use and retained across connection pool reuse cycles.

### Shared Mode

Each reactor pre-allocates a contiguous slab for receive buffers:

```
┌────────────────────────────────────────────────────────────┐
│                    Buffer Ring Slab                          │
│  ┌──────────┬──────────┬──────────┬─────┬──────────┐       │
│  │ Buffer 0 │ Buffer 1 │ Buffer 2 │ ... │ Buffer N │       │
│  │ 32 KB    │ 32 KB    │ 32 KB    │     │ 32 KB    │       │
│  └──────────┴──────────┴──────────┴─────┴──────────┘       │
│  ^ slab                                                     │
│  ^ slab + 0 * bufSize                                       │
│  ^ slab + 1 * bufSize                                       │
│  ^ slab + 2 * bufSize                                       │
└────────────────────────────────────────────────────────────┘
```

**Address formula:** `bufferPtr = slab + bufferId * RecvBufferSize`

The kernel writes directly into these buffers via the buffer ring. When the handler is done, the buffer ID is returned and the same slot is reused for future receives.

## Write Slab

Each connection owns a write slab (default 16 KB):

```csharp
public Connection(int writeSlabSize = 1024 * 16)
{
    _writeSlabSize = writeSlabSize;
    _manager = new UnmanagedMemoryManager(
        (byte*)NativeMemory.AlignedAlloc((nuint)writeSlabSize, 64),
        writeSlabSize
    );
}
```

The slab lifecycle:

```
  Write(data)  →  Advance WriteTail  →  FlushAsync()  →  Reactor sends  →  Reset(Head=0, Tail=0)
```

The slab is never freed and reallocated during the connection's lifetime -- it's allocated once and reused.

### Disposal

```csharp
public void Dispose()
{
    _manager.Free();    // NativeMemory.AlignedFree
    _manager.Dispose();
}
```

## Connection Pooling

Connections can be pooled to avoid repeated unmanaged allocation. The reset methods prepare a connection for reuse:

| Method | Speed | Safety | Use When |
|--------|-------|--------|----------|
| `Clear()` | Slower | Cancels pending waiters | Connection may have outstanding async operations |
| `Clear2()` | Faster | No waiter cancellation | Handler has definitely exited |

Both methods:
- Increment `_generation` (invalidates stale ValueTask tokens)
- Set `_closed = 1`
- Clear the SPSC receive ring
- Reset write buffer state

The generation counter is the key safety mechanism: any ValueTask created before the reset will observe a mismatched token and return `Closed` instead of accessing stale data.

## ReadOnlySequence Construction

When received data spans multiple kernel buffers, `UnmanagedMemoryManager` instances are linked into a `ReadOnlySequence<byte>`:

```csharp
public static ReadOnlySequence<byte> ToReadOnlySequence(this UnmanagedMemoryManager[] managers)
```

This creates a chain of `RingSegment` objects (custom `ReadOnlySequenceSegment<byte>` subclass) that link the managed views:

```
[Manager0.Memory] → [Manager1.Memory] → [Manager2.Memory]
      ↓                    ↓                    ↓
   Segment0 ──next──▶ Segment1 ──next──▶ Segment2
```

The resulting `ReadOnlySequence<byte>` can be parsed with `SequenceReader<byte>` for efficient multi-segment processing.

## Memory Copy Helpers

The `MemoryExtensions` class provides optimized copy operations:

```csharp
// Copy from native pointer to managed Memory
void CopyFrom(this Memory<byte> dst, byte* src, int len)

// Copy from RingItem to managed Memory
void CopyFromRing(this Memory<byte> dst, ref RingItem ring)

// Copy from array of RingItems to managed Memory
int CopyFromRings(this Memory<byte> dst, RingItem[] ring)
```

All use `Buffer.MemoryCopy` for efficient native-to-managed copying with bounds checking.

## GC Pressure Analysis

| Operation | Allocations | Notes |
|-----------|------------|-------|
| `ReadAsync()` | 0 | ValueTask-based, no state machine allocation |
| `FlushAsync()` | 0 | ValueTask-based |
| `Write(span)` | 0 | Direct memcpy to unmanaged slab |
| `GetSpan() + Advance()` | 0 | Direct pointer arithmetic |
| `TryGetRing()` | 0 | Returns struct by value |
| `ReturnRing()` | 0 | Enqueues ushort to MPSC queue |
| `ring.AsSpan()` | 0 | Creates Span over existing pointer |
| `GetAllSnapshotRingsAsUnmanagedMemory()` | 1 array | Allocates UnmanagedMemoryManager[] |
| `ToReadOnlySequence()` | N segments | Allocates RingSegment per buffer |
| `ConnectionStream.ReadAsync()` | 1 array + segments | Copies data to managed buffer |

For the lowest allocation rate, use `TryGetRing()` in a loop and process data via `ring.AsSpan()`.
