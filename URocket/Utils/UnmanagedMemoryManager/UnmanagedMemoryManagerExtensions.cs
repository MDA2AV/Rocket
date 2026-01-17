namespace URocket.Utils.UnmanagedMemoryManager;

public static class UnmanagedMemoryManagerExtensions
{
    public static void ReturnRingBuffers(this UnmanagedMemoryManager[] managers, Engine.Engine.Reactor reactor) {
        foreach (var manager in managers) {
            reactor.EnqueueReturnQ(manager.BufferId);
        }
    }
}