using System.Runtime.InteropServices;
using ioxide.utils;

namespace ioxide;

public sealed unsafe partial class Connection
{
    private readonly Reactor _reactor;

    public int ClientFd { get; private set; }

    /// <summary>The listener port this connection was accepted on; set per accept.</summary>
    public ushort ListenerPort { get; internal set; }

    /// <summary>
    /// Whether this connection sends with SEND_ZC (zero-copy). Bound at accept from
    /// <see cref="ServerConfig.ZeroCopySend"/>; kTLS forces it back to plain via the
    /// <see cref="SendOpFlags"/> setter. The reactor branches on this bool per send instead of
    /// dispatching through an indirect function pointer.
    /// </summary>
    internal bool UseZc;

    private uint _sendOpFlags = 0x100;   // MSG_WAITALL

    /// <summary>
    /// op_flags for sends on this connection. Defaults to MSG_WAITALL (the kernel coalesces short
    /// sends into one CQE). kTLS rejects MSG_WAITALL, so <c>ioxide.tls</c> clears this after the
    /// handoff; the reactor's partial-send loop preserves correctness either way. Clearing it (the
    /// kTLS marker) also pins this connection to plain SEND - SEND_ZC through the TLS ULP gives no
    /// benefit and may be rejected.
    /// </summary>
    public uint SendOpFlags
    {
        get => _sendOpFlags;
        set
        {
            _sendOpFlags = value;
            if (value == 0)
            {
                UseZc = false;
            }
        }
    }

    // Bumped on Clear(); the low 16 bits serve as the IVTS token so stale awaiters
    // from a previous pool life are detectable.
    private int _generation;

    // Two owners: the reactor (recv side) and the handler. Init 2 on accept; teardown
    // runs only at 0, so a connection is never recycled under a live handler.
    private int _refs;

    public Connection(Reactor reactor, int fd, int writeSlabSize = 1024 * 16, int recvQueueEntries = 64)
    {
        _reactor = reactor;
        ClientFd = fd;
        _writeSlabSize = writeSlabSize;
        WriteBuffer = (byte*)NativeMemory.AlignedAlloc((nuint)writeSlabSize, 64);
        _recv = new SpscRecvRing(recvQueueEntries);

        _manager = new UnmanagedMemoryManager(WriteBuffer, writeSlabSize);
    }

    // Wake awaiters with closed=1. Reactor-thread or teardown paths only.
    public void MarkClosed()
    {
        Volatile.Write(ref _closed, 1);

        if (Interlocked.Exchange(ref _armed, 0) == 1)
        {
            _readSignal.SetResult(new RecvSnapshot(_recv.SnapshotTail(), isClosed: true));
        }
        else
        {
            Volatile.Write(ref _pending, 1);
        }

        if (Interlocked.Exchange(ref _flushArmed, 0) == 1)
        {
            Volatile.Write(ref _flushInProgress, 0);
            _flushSignal.SetResult(true);
        }
    }

    internal void InitRefs() => Volatile.Write(ref _refs, 2);

    /// <summary>Release one owner's ref; whoever hits 0 hands the connection to the reactor for recycle.</summary>
    public void DecRef()
    {
        if (Interlocked.Decrement(ref _refs) == 0)
        {
            _reactor.EnqueueRecycle(this);
        }
    }

    internal void Clear()
    {
        // Bump generation first so stale IVTS tokens resolve to Closed()/no-op.
        Interlocked.Increment(ref _generation);

        Volatile.Write(ref _armed, 0);
        Volatile.Write(ref _pending, 0);
        Volatile.Write(ref _closed, 0);
        Volatile.Write(ref _flushArmed, 0);
        Volatile.Write(ref _flushInProgress, 0);

        WriteHead = 0;
        WriteTail = 0;
        WriteInFlight = 0;
        ZcNotifPending = 0;

        _readSignal.Reset();
        _flushSignal.Reset();

        _recv.Reset();
        IncrementalMode = false;
        SendOpFlags = 0x100;   // MSG_WAITALL; a kTLS connection re-sets this per handshake
        ListenerPort = 0;
    }

    public void Dispose()
    {
        if (WriteBuffer != null)
        {
            NativeMemory.AlignedFree(WriteBuffer);
            WriteBuffer = null;
        }
        DisposeIncremental();
    }
}
