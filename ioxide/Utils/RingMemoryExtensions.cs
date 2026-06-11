using System.Buffers;

namespace ioxide.utils;

public static class RingMemoryExtensions
{
    /// <summary>
    /// Stitch the snapshot's buffers into one logical byte sequence, so a parser sees the request
    /// whole no matter how many recv slices it arrived in. Zero-copy: segments point straight at
    /// the ring buffers, so parse before returning them.
    /// </summary>
    public static ReadOnlySequence<byte> ToReadOnlySequence(this UnmanagedMemoryManager[] memories)
    {
        if (memories.Length == 0)
        {
            return ReadOnlySequence<byte>.Empty;
        }

        if (memories.Length == 1)
        {
            return new ReadOnlySequence<byte>(memories[0].Memory);
        }

        var head = new RingSegment(memories[0].Memory, memories[0].BufferId);
        RingSegment tail = head;
        for (int i = 1; i < memories.Length; i++)
        {
            tail = tail.Append(memories[i].Memory, memories[i].BufferId);
        }

        return new ReadOnlySequence<byte>(head, 0, tail, tail.Memory.Length);
    }
}
