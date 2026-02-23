using System.Runtime.CompilerServices;

namespace zerg;

public unsafe partial class Connection
{
    /// <summary>
    /// Advances the logical tail after direct buffer writes using <see cref="GetSpan"/> / <see cref="GetMemory"/>.
    /// Caller must ensure they wrote exactly <paramref name="count"/> bytes into the returned buffer.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Advance(int count)
    {
        if (Volatile.Read(ref _flushInProgress) != 0)
            throw new InvalidOperationException("Cannot write while flush is in progress.");
        
        WriteTail += count;
    }

    // ** IBufferWriter Implementation **
    /// <summary>
    /// Returns a writable <see cref="Memory{Byte}"/> view into the remaining slab.
    /// </summary>
    public Memory<byte> GetMemory(int sizeHint = 0) 
    {
        if (Volatile.Read(ref _flushInProgress) != 0)
            throw new InvalidOperationException("Cannot write while flush is in progress.");
        
        int remaining = _writeSlabSize - WriteTail;
        if (sizeHint > remaining)
            throw new InvalidOperationException("Buffer too small.");
        
        return _manager.Memory.Slice(WriteTail, remaining);
    }
    
    /// <summary>
    /// Returns a writable <see cref="Span{Byte}"/> view into the remaining slab.
    /// </summary>
    public Span<byte> GetSpan(int sizeHint = 0) 
    {
        if (Volatile.Read(ref _flushInProgress) != 0)
            throw new InvalidOperationException("Cannot write while flush is in progress.");
        
        if (WriteTail + sizeHint > _writeSlabSize)
            throw new InvalidOperationException("Buffer too small.");

        return new Span<byte>(WriteBuffer + WriteTail, _writeSlabSize - WriteTail);
    }
}