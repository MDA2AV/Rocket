using System.Runtime.InteropServices;

namespace zerg.core;

public static class ReadOnlySpanExtensions
{
    public static unsafe UnmanagedMemoryManager ToUnmanagedMemoryManager(this ReadOnlySpan<byte> source)
    {
        byte* ptr = (byte*)NativeMemory.AlignedAlloc((nuint)source.Length, 64);
        if (ptr == null) throw new OutOfMemoryException();

        // copy source -> unmanaged slab
        source.CopyTo(new Span<byte>(ptr, source.Length));

        return new UnmanagedMemoryManager(ptr, source.Length);
    }
}
