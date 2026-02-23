using System.Buffers;

namespace zerg.Utils.UnmanagedMemoryManager;

public static class UnmanagedMemoryManagerExtensions
{
    public static void ReturnRingBuffers(this UnmanagedMemoryManager[] managers, Engine.Engine.Reactor reactor) {
        foreach (var manager in managers) {
            reactor.EnqueueReturnQ(manager.BufferId);
        }
    }
    
    public static ReadOnlySequence<byte> ToReadOnlySequence(this UnmanagedMemoryManager[] managers) {
        ArgumentNullException.ThrowIfNull(managers);
        if (managers.Length == 0) return ReadOnlySequence<byte>.Empty;
        
        var head = new RingSegment(managers[0].Memory,  managers[0].BufferId);
        var tail = head;
        
        for (var i = 1; i < managers.Length; i++) tail = tail.Append(managers[i].Memory,  managers[i].BufferId);
        
        return new ReadOnlySequence<byte>(head, 0, tail, tail.Memory.Length);
    }
}