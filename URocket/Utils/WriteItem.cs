namespace URocket.Utils;

public readonly unsafe struct WriteItem {
    public UnmanagedMemoryManager.UnmanagedMemoryManager Buffer { get; }
    public int ClientFd { get; }

    public WriteItem(UnmanagedMemoryManager.UnmanagedMemoryManager buffer, int clientFd){
        Buffer = buffer;
        ClientFd = clientFd;
    }
}