namespace URocket.Utils;

public readonly unsafe struct RecvItem {
    public readonly byte* Ptr;
    public readonly int Length;
    public readonly ushort BufferId;

    public RecvItem(byte* ptr, int length, ushort bufferId) {
        Ptr = ptr;
        Length = length;
        BufferId = bufferId;
    }

    public ReadOnlySpan<byte> AsSpan() => new(Ptr, Length);
    
    public UnmanagedMemoryManager.UnmanagedMemoryManager AsUnmanagedMemoryManager() => new(Ptr, Length,  BufferId);
}