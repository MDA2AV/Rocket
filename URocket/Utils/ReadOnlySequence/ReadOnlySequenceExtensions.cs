using System.Buffers;

namespace URocket.Utils.ReadOnlySequence;

public static class ReadOnlySequenceExtensions {
    public static ReadOnlySequence<byte> ToReadOnlySequence(this UnmanagedMemoryManager.UnmanagedMemoryManager[] managers) {
        ArgumentNullException.ThrowIfNull(managers);
        if (managers.Length == 0) return ReadOnlySequence<byte>.Empty;
        
        var head = new BufferSegment(managers[0].Memory);
        var tail = head;
        
        for (var i = 1; i < managers.Length; i++) tail = tail.Append(managers[i].Memory);
        
        return new ReadOnlySequence<byte>(head, 0, tail, tail.Memory.Length);
    }
}
