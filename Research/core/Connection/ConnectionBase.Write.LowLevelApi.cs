using System.Runtime.CompilerServices;

namespace zerg.core;

public unsafe partial class ConnectionBase
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Write(byte* ptr, int length)
    {
        if (Volatile.Read(ref _flushInProgress) != 0)
            throw new InvalidOperationException("Cannot write while flush is in progress.");

        if ((uint)length > (uint)_writeSlabSize)
            throw new ArgumentOutOfRangeException(nameof(length));

        if (WriteTail + length > _writeSlabSize)
            throw new InvalidOperationException("Buffer too small.");

        Buffer.MemoryCopy(
            source: ptr,
            destination: WriteBuffer + WriteTail,
            destinationSizeInBytes: _writeSlabSize - WriteTail,
            sourceBytesToCopy: length);

        WriteTail += length;
    }
}
