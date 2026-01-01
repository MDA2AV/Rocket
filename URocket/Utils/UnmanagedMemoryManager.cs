using System.Buffers;

namespace URocket.Utils;

/*
   unsafe
   {
       byte* ptr = /* from recv, buffer ring, slab, etc * /;
       int len   = /* received length * /;
   
       var manager = new UnmanagedMemoryManager(ptr, len);
   
       ReadOnlyMemory<byte> memory = manager.Memory; // ✅ zero allocation
   }
 */

public unsafe sealed class UnmanagedMemoryManager : MemoryManager<byte>
{
    private readonly byte* _ptr;
    private readonly int _length;

    public UnmanagedMemoryManager(byte* ptr, int length)
    {
        _ptr = ptr;
        _length = length;
    }

    public override Span<byte> GetSpan()
        => new Span<byte>(_ptr, _length);

    public override MemoryHandle Pin(int elementIndex = 0)
        => new MemoryHandle(_ptr + elementIndex);

    public override void Unpin() { }

    protected override void Dispose(bool disposing) { }
}