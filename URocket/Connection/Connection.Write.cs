using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using URocket.Utils;
using URocket.Utils.UnmanagedMemoryManager;

namespace URocket.Connection;

/// <remarks>
/// <br/>
/// **Write Remarks**
/// This type implements a fixed-size unmanaged slab used as a staging buffer
/// for outgoing data. Writes append into the slab and are later flushed by
/// the reactor thread. No internal synchronization is performed: this instance
/// is expected to be owned by a single reactor at a time.
///
/// Invariants:
/// - Writes append at <see cref="WriteTail"/>.
/// - The buffer is flushed when <see cref="CanWrite"/> is set.
/// - After a flush completes, <see cref="ResetWriteBuffer"/> must be called
///   before the buffer can be reused.
/// <br/>
/// </remarks>
public unsafe partial class Connection : IBufferWriter<byte>, IDisposable 
{
    /// <summary>Size of the unmanaged write slab.</summary>
    private readonly int _writeSlabSize;

    /// <summary>Memory owner for the unmanaged write slab.</summary>
    private readonly UnmanagedMemoryManager _manager;

    /// <summary>Pointer to the unmanaged write buffer.</summary>
    public byte* WriteBuffer { get; private set; }

    /// <summary>Logical start of valid data (currently unused; reserved for future streaming).</summary>
    public int WriteHead { get; set; }

    /// <summary>Logical end of written data in <see cref="WriteBuffer"/>.</summary>
    public int WriteTail { get; set; }

    /// <summary>Indicates that the buffer may be flushed by the reactor.</summary>
    public volatile bool CanFlush = true;

    /// <summary>Indicates that new data is available for sending.</summary>
    public volatile bool CanWrite = false;

    /// <summary>
    /// Creates a new unmanaged write slab for the connection.
    /// </summary>
    /// <param name="writeSlabSize">Size in bytes of the slab.</param>
    public Connection(int writeSlabSize = 1024 * 16) {
        _writeSlabSize = writeSlabSize;
        WriteBuffer = (byte*)NativeMemory.AlignedAlloc((nuint)(writeSlabSize), 64);
        WriteHead = 0; WriteTail = 0;
        
        _manager = new UnmanagedMemoryManager(WriteBuffer, writeSlabSize);
    }
    /// <summary>
    /// Copies unmanaged memory into the write slab.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(byte* ptr, int length)
    {
        if ((uint)length > (uint)_writeSlabSize) // also rejects negative
            throw new ArgumentOutOfRangeException(nameof(length));

        if (WriteTail + length > _writeSlabSize)
            throw new InvalidOperationException("Buffer too small.");

        // copy unmanaged -> unmanaged
        Buffer.MemoryCopy(
            source: ptr,
            destination: WriteBuffer + WriteTail,
            destinationSizeInBytes: _writeSlabSize - WriteTail,
            sourceBytesToCopy: length);

        WriteTail += length;
    }
    /// <summary>
    /// Appends a managed buffer into the write slab.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(ReadOnlyMemory<byte> source)
    {
        int len = source.Length;
        if (WriteTail + len > _writeSlabSize)
            throw new InvalidOperationException("Buffer too small.");

        source.Span.CopyTo(
            new Span<byte>(WriteBuffer + WriteTail, len)
        );

        WriteTail += len;
    }
    /// <summary>
    /// Appends a span into the write slab.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(ReadOnlySpan<byte> source) {
        int len = source.Length;
        if (WriteTail + len > _writeSlabSize)
            throw new InvalidOperationException("Buffer too small.");

        source.CopyTo(new Span<byte>(WriteBuffer + WriteTail, len));
        WriteTail += len;
    }
    /// <summary>
    /// Enqueues a write operation to be processed by the reactor.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Write(WriteItem item) => Reactor.TryEnqueueWrite(item);
    /// <summary>
    /// Signals that the current contents should be flushed by the reactor.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Flush() => CanWrite = true;
    /// <summary>
    /// Advances the logical tail after direct buffer writes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Advance(int count) => WriteTail += count;

    // ** IBufferWriter Implementation **
    /// <summary>
    /// Returns a writable <see cref="Memory{Byte}"/> view into the remaining slab.
    /// </summary>
    public Memory<byte> GetMemory(int sizeHint = 0) {
        int remaining = _writeSlabSize - WriteTail;
        if (sizeHint > remaining)
            throw new InvalidOperationException("Buffer too small.");
        
        return _manager.Memory.Slice(WriteTail, remaining);
    }
    /// <summary>
    /// Returns a writable <see cref="Span{Byte}"/> view into the remaining slab.
    /// </summary>
    public Span<byte> GetSpan(int sizeHint = 0) {
        if (WriteTail + sizeHint > _writeSlabSize)
            throw new InvalidOperationException("Buffer too small.");

        return new Span<byte>(WriteBuffer + WriteTail, _writeSlabSize - WriteTail);
    }
    
    // ** Reset and Dispose **
    /// <summary>
    /// Resets the slab so it can be reused for the next batch.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ResetWriteBuffer() { WriteHead = 0; WriteTail = 0; }
    /// <summary>
    /// Frees the unmanaged slab and releases the memory manager.
    /// </summary>
    public void Dispose() {
        _manager.Free();
        ((IDisposable)_manager).Dispose();
    }
}