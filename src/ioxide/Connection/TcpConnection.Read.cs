using ioxide.utils;

// ReSharper disable SuggestVarOrType_BuiltInTypes

namespace ioxide;

/// <summary>
/// TCP read side: the buffer-ring enqueue (<see cref="Complete"/>) and buffer returns that feed the
/// transport-neutral read surface on <see cref="Connection"/> (ReadAsync + the IVTS live there).
/// </summary>
public sealed unsafe partial class TcpConnection
{
    internal TcpConnection SetFd(int fd)
    {
        ClientFd = fd;
        return this;
    }

    /// <summary>Return every buffer obtained from <see cref="Connection.GetSnapshotMemories"/> to its ring.</summary>
    public void ReturnBuffers(UnmanagedMemoryManager[] memories)
    {
        foreach (UnmanagedMemoryManager memory in memories)
        {
            if (IncrementalMode)
            {
                _reactor.EnqueueReturnQIncremental(ClientFd, memory.Gen, memory.BufferId);
            }
            else
            {
                _reactor.EnqueueReturnQ(memory.BufferId);
            }
        }
    }

    // Enqueue one buffer-ring recv into the shared queue, then wake the reader. Returns false on
    // recv-queue overflow; the reactor then tears the connection down.
    public bool Complete(int res, ushort bid, bool hasBuffer, byte* ptr)
    {
        if (!_recv.TryEnqueue(new SpscRecvRing.Item
                 {
                     Ptr = ptr,
                     Bid = bid,
                     Len = res,
                     HasBuffer = hasBuffer,
                     Gen = (ushort)Generation
                 }))
        {
            Console.Error.WriteLine("[conn] recv queue overflow; closing connection.");
            if (hasBuffer && !IncrementalMode)
            {
                _reactor.ReturnBufferDirect(bid);   // per-conn rings are freed wholesale instead
            }
            return false;
        }

        SignalDataArrived();
        return true;
    }

    internal void DrainRecv()
    {
        // Return buffer IDs still sitting in the SPSC ring at teardown.
        while (_recv.TryDequeue(out SpscRecvRing.Item item))
        {
            if (item.HasBuffer)
            {
                _reactor.ReturnBufferDirect(item.Bid);
            }
        }
    }
}
