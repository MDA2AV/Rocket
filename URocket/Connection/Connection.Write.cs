using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using URocket.Utils.UnmanagedMemoryManager;

namespace URocket.Connection;

public unsafe partial class Connection : IBufferWriter<byte>, IDisposable {
    private readonly int _writeSlabSize;
    private readonly UnmanagedMemoryManager _manager;
    
    public byte* WriteBuffer { get; private set; }
    public int WriteHead { get; set; }
    public int WriteTail { get; set; }

    public volatile bool Flushable = false;

    public Connection(int writeSlabSize = 1024 * 16) {
        _writeSlabSize = writeSlabSize;
        WriteBuffer = (byte*)NativeMemory.AlignedAlloc((nuint)(writeSlabSize), 64);
        WriteHead = 0; WriteTail = 0;
        
        _manager = new UnmanagedMemoryManager(WriteBuffer, writeSlabSize);
    }
    
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
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(ReadOnlySpan<byte> source) {
        int len = source.Length;
        if (WriteTail + len > _writeSlabSize)
            throw new InvalidOperationException("Buffer too small.");

        source.CopyTo(new Span<byte>(WriteBuffer + WriteTail, len));
        WriteTail += len;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Flush() {
        Reactor.Send(ClientFd, WriteBuffer, (uint)WriteHead, (uint)WriteTail);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Advance(int count) => WriteTail += count;

    public Memory<byte> GetMemory(int sizeHint = 0) {
        int remaining = _writeSlabSize - WriteTail;
        if (sizeHint > remaining)
            throw new InvalidOperationException("Buffer too small.");
        
        return _manager.Memory.Slice(WriteTail, remaining);
    }
    
    public Span<byte> GetSpan(int sizeHint = 0) {
        if (WriteTail + sizeHint > _writeSlabSize)
            throw new InvalidOperationException("Buffer too small.");

        return new Span<byte>(WriteBuffer + WriteTail, _writeSlabSize - WriteTail);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ResetWriteBuffer() { WriteHead = 0; WriteTail = 0; }

    public void Dispose() {
        _manager.Free();
        ((IDisposable)_manager).Dispose();
    }
}