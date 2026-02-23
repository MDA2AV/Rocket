namespace zerg.Utils;

public readonly struct WriteItem(UnmanagedMemoryManager.UnmanagedMemoryManager buffer, int clientFd)
{
    public UnmanagedMemoryManager.UnmanagedMemoryManager Buffer { get; } = buffer;
    public int ClientFd { get; } = clientFd;
}