using System.Buffers;

namespace MinimaZero;

/// <summary>
/// Exposes one zcrx chunk (NIC-DMA'd payload living inside the registered
/// area) as <see cref="Memory{T}"/> so it composes with BCL async APIs.
/// <para>
/// Still allocated per recv in this teaching code (see Part 3 discussion).
/// The zcrx switch makes the *data* genuinely zero-copy; it does not by
/// itself remove this wrapper allocation. To claim zero userspace alloc as
/// well, pool one of these per <see cref="Connection"/> and re-point it.
/// </para>
/// </summary>
internal sealed unsafe class UnmanagedMemoryManager : MemoryManager<byte>
{
    private readonly byte* _ptr;
    private readonly int _length;

    /// <summary>Full rcqe.off token, written verbatim back into the refill
    /// queue to recycle this chunk.</summary>
    public ulong Off { get; }

    public int Length => _length;

    public UnmanagedMemoryManager(byte* ptr, int length, ulong off)
    {
        _ptr = ptr;
        _length = length;
        Off = off;
    }

    public override Span<byte> GetSpan() => new(_ptr, _length);

    public override MemoryHandle Pin(int elementIndex = 0) => new(_ptr + elementIndex);

    public override void Unpin() { }

    protected override void Dispose(bool disposing) { }
}
