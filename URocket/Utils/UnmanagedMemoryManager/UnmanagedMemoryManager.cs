using System.Buffers;
using System.Runtime.InteropServices;

namespace URocket.Utils.UnmanagedMemoryManager;

/*
   unsafe
   {
       byte* ptr = /* from recv, buffer ring, slab, etc * /;
       int len   = /* received length * /;
   
       var manager = new UnmanagedMemoryManager(ptr, len);
   
       ReadOnlyMemory<byte> memory = manager.Memory; // zero allocation
   }
 */

public sealed unsafe class UnmanagedMemoryManager : MemoryManager<byte> {
    private readonly bool _freeable = true;
    
    private readonly byte* _ptr;
    private readonly int _length;
    public ushort BufferId { get; }
    
    public byte* Ptr => _ptr;
    public int Length => _length;

    public UnmanagedMemoryManager(byte* ptr, int length) { _ptr = ptr; _length = length; }
    public UnmanagedMemoryManager(byte* ptr, int length, bool freeable) { _ptr = ptr; _length = length; _freeable = freeable; }
    public UnmanagedMemoryManager(byte* ptr, int length, ushort bufferId) { _ptr = ptr; _length = length; BufferId = bufferId; }
    public UnmanagedMemoryManager(byte* ptr, int length, ushort bufferId, bool freeable) { _freeable = freeable; _ptr = ptr; _length = length; BufferId = bufferId; }

    public override Span<byte> GetSpan() => new Span<byte>(_ptr, _length);

    public override MemoryHandle Pin(int elementIndex = 0) => new MemoryHandle(_ptr + elementIndex);

    public override void Unpin() { }

    public void Free() {
        if (!_freeable) return;
        if (_ptr != null) NativeMemory.AlignedFree(_ptr); 
    }

    protected override void Dispose(bool disposing) { }
}