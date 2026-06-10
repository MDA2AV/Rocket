using System.Buffers;
using KestrelMinima.Utils;

// ReSharper disable SuggestVarOrType_BuiltInTypes

namespace KestrelMinima;

/// <summary>
/// Fire-and-forget write path: FlushAsync hands the slab to the reactor (an io_uring
/// send SQE + eventfd wake) and returns synchronously. No IValueTaskSource, no
/// awaiter scheduling, no continuation hop. Safe for HTTP/1.1 plaintext because the
/// client cannot send the next request until it receives the previous response —
/// which in turn cannot happen until the kernel finishes our send (and the reactor
/// has processed the resulting send CQE, which is what resets WriteHead/WriteTail).
/// So by the time Kestrel produces the next response into this slab, the previous
/// send is fully ack'd and the slab is free for reuse.
/// </summary>
public sealed unsafe partial class Connection : IBufferWriter<byte>
{
    private readonly int _writeSlabSize;
    internal byte* WriteBuffer;
    // WriteHead     — bytes ack'd by the kernel (reactor thread mutates).
    // WriteSubmitted — bytes queued to the kernel via SubmitSend (reactor thread mutates).
    // WriteTail      — bytes produced by Kestrel into the slab (Kestrel thread mutates).
    internal int   WriteHead;
    internal int   WriteSubmitted;
    internal int   WriteTail;

    private readonly UnmanagedMemoryManager _manager;

    // IBufferWriter<byte>
#region IBufferWriter<byte>

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        int remaining = _writeSlabSize - WriteTail;
        if (sizeHint > remaining)
        {
            throw new InvalidOperationException(
                $"GetMemory: sizeHint={sizeHint} > remaining={remaining} (slab={_writeSlabSize}, WriteTail={WriteTail}, WriteSubmitted={WriteSubmitted}, WriteHead={WriteHead}, closed={Volatile.Read(ref _closed)})");
        }

        return _manager.Memory.Slice(WriteTail, remaining);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        if (WriteTail + sizeHint > _writeSlabSize)
        {
            throw new InvalidOperationException(
                $"GetSpan: sizeHint={sizeHint}, WriteTail={WriteTail}, slab={_writeSlabSize}, WriteSubmitted={WriteSubmitted}, WriteHead={WriteHead}, closed={Volatile.Read(ref _closed)}");
        }

        return new Span<byte>(WriteBuffer + WriteTail, _writeSlabSize - WriteTail);
    }

    public void Advance(int count) => WriteTail += count;

#endregion

    // Write to the inner buffer
    public void Write(ReadOnlySpan<byte> source)
    {
        int len = source.Length;
        if (WriteTail + len > _writeSlabSize)
        {
            throw new InvalidOperationException("Write buffer too small.");
        }

        source.CopyTo(new Span<byte>(WriteBuffer + WriteTail, len));
        WriteTail += len;
    }

    // Fire-and-forget: hand the fd to the reactor and return. The reactor reads
    // [WriteSubmitted, WriteTail) on drain and submits an SQE. Multi-flush within
    // one response is handled naturally — the MPSC may have the fd queued multiple
    // times, but the second drain finds end <= begin and no-ops.
    public ValueTask FlushAsync()
    {
        if (Volatile.Read(ref _closed) == 1)
        {
            return default;
        }

        if (WriteTail == 0)
        {
            return default;
        }

        _reactor.EnqueueFlush(ClientFd);

        return default;
    }

    // Reactor-thread: all submitted bytes ack'd AND no new bytes pending — reset.
    internal void CompleteFlush()
    {
        WriteHead = 0;
        WriteSubmitted = 0;
        WriteTail = 0;
    }
}
