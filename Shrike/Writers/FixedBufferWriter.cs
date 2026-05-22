// ReSharper disable always CheckNamespace
// ReSharper disable always SuggestVarOrType_BuiltInTypes
// (var is avoided intentionally in this project so that concrete types are visible at call sites.)
// ReSharper disable always StackAllocInsideLoop
// ReSharper disable always ClassCannotBeInstantiated

using System.Text;

#pragma warning disable CA2014

namespace Shrike;

/// <summary>
/// A high-performance, *unmanaged* buffer writer designed for scenarios where
/// zero allocations and deterministic memory layout are critical.
///
/// This struct provides a writable view over a fixed memory region (provided as
/// a raw <see cref="byte*"/> pointer). It does not own or allocate memory itself
/// — unless the caller provides it one that was manually allocated, in which
/// case <see cref="Dispose"/> can free it if desired.
///
/// The typical use case is for writing binary or HTTP data directly into a
/// pre-allocated unmanaged buffer (e.g., a native slab per connection) without
/// heap allocations or GC involvement.
/// </summary>
[SkipLocalsInit]
public unsafe class FixedBufferWriter : IUnmanagedBufferWriter<byte>, IBufferWriter<byte>, IDisposable
{
    // =========================================================================
    //  Fields
    // =========================================================================

    /// <summary>
    /// The total capacity (in bytes) of the memory region represented by this writer.
    /// </summary>
    private readonly int _capacity;
    
    private readonly UnmanagedMemoryManager _manager;

    /// <summary>
    /// The current read position (if the buffer is also reused for reads).
    /// Not used by the writer itself, but exposed for external control.
    /// </summary>
    public int Head;

    /// <summary>
    /// The current write position. Bytes have been written in [0 .. Tail).
    /// </summary>
    public int Tail;

    /// <summary>
    /// Pointer to the beginning of the unmanaged buffer.
    /// </summary>
    public byte* Ptr { get; }

    // =========================================================================
    //  Constructor
    // =========================================================================

    /// <summary>
    /// Creates a new <see cref="FixedBufferWriter"/> instance over an unmanaged
    /// memory region.
    ///
    /// <paramref name="ptr"/> must point to a memory block of at least
    /// <paramref name="capacity"/> bytes that remains valid for the lifetime
    /// of this struct.
    /// </summary>
    /// <param name="ptr">Pointer to the start of the unmanaged buffer.</param>
    /// <param name="capacity">Maximum number of bytes writable to the buffer.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public FixedBufferWriter(byte* ptr, int capacity)
    {
        Ptr = ptr;
        _capacity = capacity;
        Head = 0;
        Tail = 0;
        
        _manager = new UnmanagedMemoryManager(ptr, capacity);
    }

    // =========================================================================
    //  Core Methods
    // =========================================================================

    /// <summary>
    /// Resets both read (<see cref="Head"/>) and write (<see cref="Tail"/>)
    /// indices to zero, effectively clearing the buffer (logically).
    ///
    /// Does not modify the underlying memory — only the pointers.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Reset()
    {
        Head = 0;
        Tail = 0;
    }

    /// <summary>
    /// Advances the write pointer by <paramref name="count"/> bytes after data
    /// has been written directly into the memory region.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Advance(int count)
    {
        //Volatile.Write(ref Tail, Tail + count);
        Tail += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        int remaining = _capacity - Tail;
        if (sizeHint > remaining)
            throw new InvalidOperationException("Buffer too small.");
        
        return _manager.Memory.Slice(Tail, remaining);
    }

    /// <summary>
    /// Gets a raw unmanaged pointer to the start of the buffer.
    /// This is mainly for interop or direct native I/O operations (e.g. <c>send()</c>).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte* GetPointer() => Ptr;

    /// <summary>
    /// Returns a writable <see cref="Span{T}"/> over the remaining space in
    /// the buffer, starting at the current <see cref="Tail"/> position.
    ///
    /// Throws <see cref="InvalidOperationException"/> if the requested
    /// <paramref name="sizeHint"/> would exceed the buffer capacity.
    /// </summary>
    /// <param name="sizeHint">The minimum required size for the writable region.</param>
    public Span<byte> GetSpan(int sizeHint = 0)
    {
        if (Tail + sizeHint > _capacity)
            throw new InvalidOperationException("Buffer too small.");

        return new Span<byte>(Ptr + Tail, _capacity - Tail);
    }

    // =========================================================================
    //  Write Helpers
    // =========================================================================

    /// <summary>
    /// Copies unmanaged data directly into the buffer using a raw pointer copy.
    /// Slightly faster than <see cref="Write"/> for large spans because it avoids
    /// intermediate range checks.
    ///
    /// The caller must ensure <paramref name="source"/> does not overlap the target region.
    /// </summary>
    /// <param name="source">Data to copy into the buffer.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteUnmanaged(ReadOnlySpan<byte> source)
    {
        int len = source.Length;
        if (Tail + len > _capacity)
            throw new InvalidOperationException("Buffer too small.");

        fixed (byte* src = source)
            Buffer.MemoryCopy(src, Ptr + Tail, _capacity - Tail, len);
        
        Tail += len;
    }

    public void WriteUnmanaged(string source)
    {
        var span = new Span<byte>(Ptr + Tail, _capacity - Tail);
        var bytesWritten = Encoding.UTF8.GetBytes(source, span);
        Tail += bytesWritten;
    }

    /// <summary>
    /// Copies data from a managed <see cref="ReadOnlySpan{T}"/> into the unmanaged buffer.
    /// This version uses <see cref="Span.CopyTo"/> which performs bounds checks
    /// and is safe for managed callers.
    /// </summary>
    /// <param name="source">The data to copy into the buffer.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(ReadOnlySpan<byte> source)
    {
        int len = source.Length;
        if (Tail + len > _capacity)
            throw new InvalidOperationException("Buffer too small.");

        source.CopyTo(new Span<byte>(Ptr + Tail, _capacity - Tail));
        Tail += len;
    }

    // =========================================================================
    //  Disposal
    // =========================================================================

    /// <summary>
    /// Releases the unmanaged memory associated with this writer if it owns the pointer.
    ///
    /// If <see cref="Ptr"/> points to a shared memory region (e.g. part of a
    /// connection pool or slab allocator), calling this will free that memory
    /// globally — causing use-after-free crashes for other users.
    ///
    /// Only call this when you know this instance *owns* the buffer and no one else
    /// references it.
    /// </summary>
    public void Dispose()
    {
        if (Ptr != null)
            NativeMemory.AlignedFree(Ptr);
    }
}