using zerg.core;

namespace zerg.Utils;

public readonly struct WriteItem(UnmanagedMemoryManager buffer, int clientFd)
{
    public UnmanagedMemoryManager Buffer { get; } = buffer;
    public int ClientFd { get; } = clientFd;
}