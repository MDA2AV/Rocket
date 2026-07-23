using System.Runtime.InteropServices;
using ioxide.utils;

namespace ioxide;

public sealed unsafe partial class TcpConnection : Connection
{
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

    public TcpConnection(Reactor reactor, int fd, int writeSlabSize = 1024 * 16, int recvQueueEntries = 64, WriteOverflowStrategy overflow = WriteOverflowStrategy.Grow)
        : base(reactor, recvQueueEntries)
    {
        ClientFd = fd;
        _writeSlabSize = writeSlabSize;
        _baseSlabSize = writeSlabSize;
        _overflow = overflow;
        WriteBuffer = (byte*)NativeMemory.AlignedAlloc((nuint)writeSlabSize, 64);

        _manager = new UnmanagedMemoryManager(WriteBuffer, writeSlabSize);
    }

    // Refcount zero: the reactor owns the buffer-ring return, close(fd) and pool return (Recycle).
    protected override void EnqueueForRecycle() => _reactor.EnqueueRecycle(this);

    // Close-time: also disarm and complete a pending flush so a parked FlushAsync unwinds.
    protected override void OnMarkClosed()
    {
        if (Interlocked.Exchange(ref _flushArmed, 0) == 1)
        {
            Volatile.Write(ref _flushInProgress, 0);
            _flushSignal.SetResult(true);
        }
    }

    // Pool reuse: reset the write slab, overflow segments and flush signal (the base already reset
    // the read side and bumped the generation).
    protected override void OnClear()
    {
        Volatile.Write(ref _flushArmed, 0);
        Volatile.Write(ref _flushInProgress, 0);

        WriteHead = 0;
        WriteTail = 0;
        WriteInFlight = 0;
        ZcNotifPending = 0;

        // Segmented: hand any overflow slabs back to the pool (a connection recycled mid-response).
        if (_ovCount > 0)
        {
            ReleaseOverflow();
        }

        // Restore an overgrown write slab to its base size so the pool doesn't retain large buffers.
        if (_writeSlabSize != _baseSlabSize)
        {
            NativeMemory.AlignedFree(WriteBuffer);
            WriteBuffer = (byte*)NativeMemory.AlignedAlloc((nuint)_baseSlabSize, 64);
            _writeSlabSize = _baseSlabSize;
            _manager.Reset(WriteBuffer, _baseSlabSize);
        }

        _flushSignal.Reset();

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
        if (_iov != null)
        {
            NativeMemory.AlignedFree(_iov);
            _iov = null;
        }
        if (_msg != null)
        {
            NativeMemory.AlignedFree(_msg);
            _msg = null;
        }
        DisposeIncremental();
    }
}
