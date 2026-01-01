using System.Buffers;

namespace URocket.Utils;

public sealed class BufferSegment : ReadOnlySequenceSegment<byte>
{
    public BufferSegment(ReadOnlyMemory<byte> memory) { Memory = memory; }

    public BufferSegment Append(ReadOnlyMemory<byte> memory) {
        var next = new BufferSegment(memory) {
            RunningIndex = RunningIndex + Memory.Length
        };

        Next = next;
        return next;
    }
}