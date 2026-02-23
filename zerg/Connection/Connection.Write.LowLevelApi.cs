using System.Runtime.CompilerServices;

namespace zerg;

public unsafe partial class Connection 
{
    /// <summary>
    /// Copies unmanaged memory into the write slab.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Write(byte* ptr, int length)
    {
        if (Volatile.Read(ref _flushInProgress) != 0)
            throw new InvalidOperationException("Cannot write while flush is in progress.");
        
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
}