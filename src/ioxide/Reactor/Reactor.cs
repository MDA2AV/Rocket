using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static ioxide.Native;

namespace ioxide;

/// <summary>
/// One reactor = one thread + one io_uring + one SO_REUSEPORT listener + one connection table.
/// The reactor thread is the sole writer of the SQ, the buf_ring, and the connection table;
/// off-reactor handlers reach it through MPSC queues woken by an eventfd poll.
/// </summary>
public sealed unsafe partial class Reactor
{
    private readonly int _id;
    private Ring _ring = null!;   // created on the reactor thread (DEFER_TASKRUN requires same-thread setup+enter)

    // Connection table indexed by fd (dense small ints - array beats Dictionary per CQE).
    private Connection?[] _connections = new Connection?[4096];

    // The response-send strategy from config (ZeroCopySend), copied per-connection at accept into
    // Connection.UseZc. The send hot path branches on that bool (predictable, inlinable) instead of
    // dispatching through an indirect function pointer.
    private readonly bool _zeroCopySend;
    private readonly ushort _port;
    private readonly uint _ringEntries;
    private readonly uint _recvBufferSize;

    // user_data: [63:56] kind | [47:32] connection generation | [31:0] fd (or client-op slot).
    // The generation makes straggler CQEs from a reused fd detectable as stale.
    private const int  KindShift  = 56;
    private const int  GenShift   = 32;
    private const byte KindTcpAccept = 1;
    private const byte KindTcpRecv   = 2;
    private const byte KindTcpSend   = 3;
    private const byte KindWake      = 4;
    private const byte KindClient    = 5;   // low 32 bits = op slot (Reactor.RingHost.cs)
    private const byte KindCancel    = 6;
    private const byte KindTimer     = 7;
    private const byte KindUdpRecv   = 8;   // low 32 bits = recv-slot index (Reactor.Udp.cs)
    private const byte KindUdpSend   = 9;   // low 32 bits = send-slot index (Reactor.Udp.cs)

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Tag(byte kind, ushort gen, int fd)
        => ((ulong)kind << KindShift) | ((ulong)gen << GenShift) | (uint)fd;

    // Shared provided-buffer ring (one per reactor).
    private const ushort BgId = 1;
    private readonly uint _bufferRingEntries;   // power of two
    private byte*  _bufRing;                   // io_uring_buf_ring (kernel-shared)
    private byte*  _bufSlab;
    private uint   _bufRingMask;
    private ushort _bufRingTail;

    // Connection pool, reactor-thread-only. PoolMax × WriteSlabSize × ReactorCount bounds
    // the reserved native memory.
    private readonly int _poolMax;
    private readonly Stack<Connection> _pool;

    // Per-reactor pool of base-size write slabs, rented by connections in Segmented overflow mode.
    // Reactor-thread-only, so no locking. Capped so a burst of large responses doesn't retain memory.
    private readonly Stack<nint> _writeSlabPool = new();
    private const int WriteSlabPoolMax = 4096;

    internal nint RentWriteSlab()
        => _writeSlabPool.TryPop(out nint p) ? p : (nint)NativeMemory.AlignedAlloc((nuint)_config.WriteSlabSize, 64);

    internal void ReturnWriteSlab(nint p)
    {
        if (_writeSlabPool.Count < WriteSlabPoolMax)
        {
            _writeSlabPool.Push(p);
        }
        else
        {
            NativeMemory.AlignedFree((void*)p);
        }
    }

    // Incremental-mode sizing (see Reactor.Incremental.cs).
    private readonly int  _maxConnections;       // one bgid per active connection
    private readonly int  _connBufRingEntries;
    private readonly uint _incRecvBufferSize;

    // Transient io_uring_enter errnos.
    private const int EINTR  = 4;
    private const int EAGAIN = 11;
    private const int EBUSY  = 16;

    public Reactor(int id, ServerConfig config)
    {
        _id = id;
        _config = config;
        _port = config.Port;
        _ringEntries = config.RingEntries;
        _incremental = config.Incremental;
        _recvBufferSize = (uint)config.RecvBufferSize;
        _bufferRingEntries = (uint)config.BufferRingEntries;
        _poolMax = config.PoolMax;
        _maxConnections = config.MaxConnections;
        _connBufRingEntries = config.ConnBufRingEntries;
        _incRecvBufferSize = (uint)config.IncRecvBufferSize;
        _pool = new Stack<Connection>(config.PoolMax);
        _zeroCopySend = config.ZeroCopySend;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Connection? ConnAt(int fd, ushort gen)
    {
        Connection?[] conns = _connections;
        Connection? conn = (uint)fd < (uint)conns.Length ? conns[fd] : null;
        return conn != null && (ushort)conn.Generation == gen ? conn : null;
    }

    private void Track(int fd, Connection conn)
    {
        if (fd >= _connections.Length)
        {
            int newLength = _connections.Length;
            while (newLength <= fd)
            {
                newLength *= 2;
            }
            Array.Resize(ref _connections, newLength);
        }
        _connections[fd] = conn;
    }

    // Stage a buffer without publishing; batch drains publish once for N buffers.
    private void ReturnBufferLocal(ushort bid)
    {
        byte* slot = _bufRing + (_bufRingTail & _bufRingMask) * 16;
        *(ulong*)(slot + 0)  = (ulong)(_bufSlab + bid * (nuint)_recvBufferSize);
        *(uint*)(slot + 8)   = _recvBufferSize;
        *(ushort*)(slot + 12) = bid;
        _bufRingTail++;
        _buffersReturned = true;   // lets the loop re-arm recvs parked on -ENOBUFS (#93)
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void PublishBufRingTail()
    {
        Volatile.Write(ref *(ushort*)(_bufRing + 14), _bufRingTail);
    }

    // Reactor-thread-only; off-reactor callers use EnqueueReturnQ.
    internal void ReturnBufferDirect(ushort bid)
    {
        ReturnBufferLocal(bid);
        PublishBufRingTail();
    }

    // SQE producers - reactor-thread-only.
    private IoUringSqe* GetSqeOrFlush()
    {
        IoUringSqe* sqe = _ring.GetSqe();
        if (sqe != null)
        {
            return sqe;
        }

        // SQ is full: flush queued SQEs to the kernel (which frees ring slots) and retry. A few
        // submit rounds clear the transient fullness a large CQE batch can cause; only a genuinely
        // stuck ring falls through to throw, instead of crashing the reactor on the first miss.
        for (int attempt = 0; attempt < 16 && sqe == null; attempt++)
        {
            _ring.SubmitAndWait(0);
            sqe = _ring.GetSqe();
        }

        if (sqe == null)
        {
            throw new InvalidOperationException("io_uring SQ still full after repeated flush");
        }

        return sqe;
    }
}
