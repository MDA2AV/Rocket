namespace zerg.Utils;

public readonly unsafe struct RingItem(byte* ptr, int length, ushort bufferId)
{
    public readonly byte* Ptr = ptr;
    public readonly int Length = length;
    public readonly ushort BufferId = bufferId;

    public ReadOnlySpan<byte> AsSpan() => new(Ptr, Length);
    
    public UnmanagedMemoryManager.UnmanagedMemoryManager AsUnmanagedMemoryManager() => new(Ptr, Length,  BufferId);
}