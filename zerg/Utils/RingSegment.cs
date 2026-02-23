using System.Buffers;

namespace zerg.Utils;

public sealed class RingSegment : ReadOnlySequenceSegment<byte>
{
    public ushort BufferId { get; set; }

    public RingSegment(ReadOnlyMemory<byte> memory, ushort bufferId)
    {
        Memory = memory; 
        BufferId = bufferId;
    }

    public RingSegment Append(ReadOnlyMemory<byte> memory, ushort bufferId) 
    {
        var next = new RingSegment(memory, bufferId) 
        {
            RunningIndex = RunningIndex + Memory.Length
        };

        Next = next;
        return next;
    }
}