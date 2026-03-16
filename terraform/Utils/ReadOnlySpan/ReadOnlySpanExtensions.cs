using System.Runtime.InteropServices;

namespace terraform.Utils.ReadOnlySpan;

public static class ReadOnlySpanExtensions
{
    public static unsafe UnmanagedMemoryManager.UnmanagedMemoryManager ToUnmanagedMemoryManager(this ReadOnlySpan<byte> source)
    {
        byte* ptr = (byte*)NativeMemory.AlignedAlloc((nuint)source.Length, 64);
        if (ptr == null) throw new OutOfMemoryException();

        source.CopyTo(new Span<byte>(ptr, source.Length));

        return new UnmanagedMemoryManager.UnmanagedMemoryManager(ptr, source.Length);
    }
}
