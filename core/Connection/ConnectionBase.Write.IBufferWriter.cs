using System.Runtime.CompilerServices;

namespace zerg.core;

public unsafe partial class ConnectionBase
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Advance(int count)
    {
        if (Volatile.Read(ref _flushInProgress) != 0)
            throw new InvalidOperationException("Cannot write while flush is in progress.");

        WriteTail += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        if (Volatile.Read(ref _flushInProgress) != 0)
            throw new InvalidOperationException("Cannot write while flush is in progress.");

        int remaining = _writeSlabSize - WriteTail;
        if (sizeHint > remaining)
            throw new InvalidOperationException("Buffer too small.");

        return _manager.Memory.Slice(WriteTail, remaining);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        if (Volatile.Read(ref _flushInProgress) != 0)
            throw new InvalidOperationException("Cannot write while flush is in progress.");

        if (WriteTail + sizeHint > _writeSlabSize)
            throw new InvalidOperationException("Buffer too small.");

        return new Span<byte>(WriteBuffer + WriteTail, _writeSlabSize - WriteTail);
    }
}
