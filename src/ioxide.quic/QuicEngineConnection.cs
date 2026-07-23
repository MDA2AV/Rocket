using System.Diagnostics;
using System.Runtime.InteropServices;
using ioxide;

namespace ioxide.quic;

/// <summary>
/// A live ngtcp2 server connection, bridging the reactor's QUIC transport to the native engine.
/// Datagrams routed by CID arrive at <see cref="OnDatagram"/> and are fed to ngtcp2; the engine's
/// output is flushed back through the transport's <c>Send</c>; loss/idle deadlines ride the reactor
/// ticker via <see cref="GetNextTimeout"/> / <see cref="OnTimer"/>. Everything runs on the owning
/// reactor thread, so the whole connection - transport half and engine half - is single-threaded.
///
/// Application bytes flow through the read surface on <see cref="QuicConnection"/>: each decrypted
/// stream event is copied into the recv queue during iq_conn_read, and the IVTS fires ONCE after
/// the read returns, so the resumed handler (see <see cref="QuicOptions.Handle"/>) can never
/// re-enter the engine mid-read. Replies go out via <see cref="SendStream"/>.
/// </summary>
public unsafe class QuicEngineConnection : QuicConnection
{
    // sockaddr cap the transport uses for its peer-address blocks; large enough for sockaddr_in6.
    private const int PeerAddrCap = 128;

    private readonly QuicEngine _engine;
    private nint _conn;                        // iq_conn*, null once closed
    private GCHandle _self;                    // stable void* user passed to the shim
    private bool _handshakeDone;
    private bool _closed;

    // Held directly (not via the base class) because the engine's get_new_connection_id callback
    // fires during iq_accept - before the transport wires the base Reactor/SocketFd/PeerAddr in.
    private Reactor _reactor = null!;
    private int _socketFd;

    // One reusable send scratch datagram (max QUIC payload); the engine writes at most one per call.
    private readonly byte[] _sendBuf = new byte[1452];

    // Set inside iq_conn_read: _recvEnqueued arms the once-per-read IVTS fire, _recvOverflow defers
    // the teardown until the engine call unwinds (freeing the conn inside its own callback is UB).
    private bool _recvEnqueued;
    private bool _recvOverflow;

    public QuicEngineConnection(QuicEngine engine)
    {
        _engine = engine;
    }

    /// <summary>Handshake finished; safe to open server-initiated streams and send.</summary>
    protected virtual void OnHandshakeCompleted() { }

    // ngtcp2 delivered decrypted stream bytes (mid-iq_conn_read; the span dies when it returns).
    // Copy-and-enqueue only - the wake happens once, after the read unwinds.
    private void OnStreamData(long streamId, ReadOnlySpan<byte> data, bool fin)
    {
        if (EnqueueStreamData(streamId, data, fin))
        {
            _recvEnqueued = true;
        }
        else
        {
            _recvOverflow = true;
        }
    }

    /// <summary>A stream ended (peer FIN/reset or local close).</summary>
    protected virtual void OnStreamClosed(long streamId, ulong appErrorCode) { }

    // Monotonic nanosecond clock ngtcp2 wants; Stopwatch is monotonic, unlike wall time.
    private static ulong NowNs() => (ulong)(Stopwatch.GetTimestamp() * (1_000_000_000.0 / Stopwatch.Frequency));

    // struct sockaddr_in for 127.0.0.1:port (16 bytes): family AF_INET(2), port big-endian, addr.
    private static void FillSockaddrInLoopback(Span<byte> sa, ushort port)
    {
        sa.Clear();
        sa[0] = 2;                        // AF_INET (x86 little-endian family low byte)
        sa[2] = (byte)(port >> 8);        // sin_port, network byte order
        sa[3] = (byte)(port & 0xff);
        sa[4] = 127; sa[5] = 0; sa[6] = 0; sa[7] = 1;   // 127.0.0.1
    }

    // --- transport-facing lifecycle (called by the QUIC demux / engine factory) ---------------

    // Adopt the connection: validate the client's first datagram and create the ngtcp2 conn. Runs
    // inside the factory, before the transport records the route; returns false to reject. reactor
    // and socketFd are captured here because engine callbacks can fire during iq_accept.
    internal bool TryAccept(nint enginePtr, Reactor reactor, in UdpDatagram datagram, Span<byte> scidOut, out int scidLen)
    {
        _reactor  = reactor;
        _socketFd = datagram.SocketFd;
        _self     = GCHandle.Alloc(this);
        scidLen = 0;

        // The server's own sockaddr for the ngtcp2 path. Milestone: IPv4 loopback on the bound
        // port - a real 16-byte sockaddr_in (ngtcp2 asserts addrlen fits its address union).
        Span<byte> local = stackalloc byte[16];
        FillSockaddrInLoopback(local, datagram.LocalPort);

        fixed (byte* pkt = datagram.Payload)
        fixed (byte* scid = scidOut)
        fixed (byte* loc = local)
        {
            _conn = Ngtcp2.iq_accept(
                enginePtr,
                loc, (nuint)local.Length,
                (void*)datagram.PeerAddr, (nuint)datagram.PeerAddrLen,
                pkt, (nuint)datagram.Payload.Length,
                NowNs(), (void*)GCHandle.ToIntPtr(_self), scid);
        }

        if (_conn == 0)
        {
            _self.Free();
            return false;
        }
        scidLen = (int)_engine.CidLength;
        return true;
    }

    public override void OnDatagram(ReadOnlySpan<byte> payload, byte tos, int groSegmentSize)
    {
        if (_closed)
        {
            return;
        }
        _inEngineCycle = true;
        try
        {
            OnDatagramCore(payload, tos, groSegmentSize);
        }
        finally
        {
            _inEngineCycle = false;
            FlushGso();
        }
    }

    private void OnDatagramCore(ReadOnlySpan<byte> payload, byte tos, int groSegmentSize)
    {
        // A GRO train is several datagrams of groSegmentSize bytes (last may be shorter); feed each.
        int stride = groSegmentSize > 0 ? groSegmentSize : payload.Length;
        for (int off = 0; off < payload.Length; off += stride)
        {
            int len = Math.Min(stride, payload.Length - off);
            int rv;
            fixed (byte* p = payload.Slice(off, len))
            {
                // milestone: no migration - the path is fixed at accept, so remote_sa is unused.
                rv = Ngtcp2.iq_conn_read(_conn, null, 0, p, (nuint)len, tos, NowNs());
            }
            if (rv != 0)
            {
                CloseFromEngine(rv);
                return;
            }
            if (_recvOverflow)
            {
                // Deferred from OnStreamData: tear down now that the engine call has unwound.
                Console.Error.WriteLine("[ioxide.quic] recv queue overflow; closing connection.");
                _closed = true;
                _reactor.QuicRemoveConnection(this);
                Destroy();
                return;
            }
        }

        if (!_handshakeDone && Ngtcp2.iq_conn_is_established(_conn) != 0)
        {
            HandshakeCompletedOnce();
        }

        FlushEgress();
        FireRecv();
    }

    // Single handshake-done funnel (the engine callback and the OnDatagram poll can both detect
    // it): record the negotiated ALPN, then fire the hook.
    private void HandshakeCompletedOnce()
    {
        _handshakeDone = true;

        Span<byte> alpn = stackalloc byte[64];
        fixed (byte* p = alpn)
        {
            nuint len = Ngtcp2.iq_conn_get_alpn(_conn, p, (nuint)alpn.Length);
            if (len > 0)
            {
                NegotiatedProtocol = System.Text.Encoding.ASCII.GetString(alpn[..(int)len]);
            }
        }

        OnHandshakeCompleted();
    }

    // The once-per-read wake: the engine is idle again, so the handler resuming inline (and
    // immediately calling SendStream) is safe.
    private void FireRecv()
    {
        if (_recvEnqueued)
        {
            _recvEnqueued = false;
            SignalDataArrived();
        }
    }

    public override long GetNextTimeout(long nowMs)
    {
        if (_closed)
        {
            return long.MaxValue;
        }
        ulong expiryNs = Ngtcp2.iq_conn_expiry(_conn);
        if (expiryNs == ulong.MaxValue)
        {
            return long.MaxValue;
        }

        // Already due (it expired between ticker sweeps): fire on this sweep. The subtraction below
        // would otherwise underflow the ulong and push the deadline ~584 years out, permanently
        // killing this connection's loss recovery.
        ulong now = NowNs();
        if (expiryNs <= now)
        {
            return nowMs;
        }

        // The sweep works in TickCount64 ms; convert the ns deadline to that clock's frame.
        return nowMs + (long)((expiryNs - now) / 1_000_000);
    }

    public override void OnTimer(long nowMs)
    {
        if (_closed)
        {
            return;
        }
        _inEngineCycle = true;
        try
        {
            int rv = Ngtcp2.iq_conn_handle_expiry(_conn, NowNs());
            if (rv != 0)
            {
                CloseFromEngine(rv);
                return;
            }
            FlushEgress();
            FireRecv();
        }
        finally
        {
            _inEngineCycle = false;
            FlushGso();
        }
    }

    public override void OnEvicted(QuicEvictReason reason)
    {
        Destroy();
    }

    // --- application-facing send --------------------------------------------------------------

    // Application egress the engine couldn't take yet (cwnd/pacing/flow control full), replayed
    // from FlushEgress once ACKs or timers free the window. Order-preserving; bytes are NEVER
    // dropped - a response the engine defers would otherwise vanish for good, since the layer
    // above (nghttp3) has already accounted it as written and will not re-emit it.
    private readonly Queue<(long Sid, byte[] Data, bool Fin)> _pendingSend = new();
    private long   _pendingSid;
    private byte[]? _pendingData;   // partially-written head; null = take the next queue entry
    private int    _pendingOff;
    private bool   _pendingFin;
    private int    _pendingBytes;

    // A peer that stops draining gets closed rather than buffered without bound.
    private const int PendingSendCap = 1 << 20;

    /// <summary>Queue bytes on a stream and flush. streamId must come from a delivered item or
    /// <see cref="OpenUniStream"/>. fin closes the send side. Never discards: what the engine
    /// can't take now is buffered and replayed as the window opens.</summary>
    public override void SendStream(long streamId, ReadOnlySpan<byte> data, bool fin)
    {
        if (_closed)
        {
            return;
        }

        if (_pendingData is null && _pendingSend.Count == 0)
        {
            int consumed = WriteStream(streamId, data, fin, out bool complete);
            if (_closed)
            {
                return;
            }
            FlushConnection();
            if (complete)
            {
                return;
            }
            data = data[consumed..];
        }

        // Preserve order behind whatever is already queued.
        _pendingBytes += data.Length;
        if (_pendingBytes > PendingSendCap)
        {
            Console.Error.WriteLine("[ioxide.quic] send backlog cap exceeded; closing connection.");
            _closed = true;
            _reactor.QuicRemoveConnection(this);
            Destroy();
            return;
        }
        _pendingSend.Enqueue((streamId, data.ToArray(), fin));
    }

    /// <summary>Open a server-initiated unidirectional stream (H3 control / QPACK); id, or negative.</summary>
    public override long OpenUniStream()
    {
        if (_closed)
        {
            return -1;
        }
        return Ngtcp2.iq_conn_open_uni(_conn);
    }

    // --- GSO send batching ----------------------------------------------------------------------
    // One sendmsg per engine cycle instead of one per datagram: ngtcp2 emits runs of equal-size
    // (MTU-full) datagrams under load, which is exactly the UDP_SEGMENT shape. A shorter datagram
    // may only END a batch (GSO semantics: equal segments, the last may be short). All datagrams
    // in a batch are this connection's, so the destination is single by construction.

    private readonly byte[] _gsoBuf = new byte[63 * 1024];   // < 65507, the UDP payload ceiling
    private int  _gsoLen;
    private int  _gsoSeg;      // segment size = first datagram's length; 0 = batch empty
    private bool _gsoClosed;   // a short (final) segment landed - flush before accepting more
    private bool _inEngineCycle;

    private void QueueSend(ReadOnlySpan<byte> datagram)
    {
        if (!_inEngineCycle)
        {
            Send(datagram);   // outside a cycle (mailbox-resumed handler): direct, unbatched
            return;
        }

        int len = datagram.Length;
        if (_gsoSeg != 0 && (len > _gsoSeg || _gsoClosed || _gsoLen + len > _gsoBuf.Length))
        {
            FlushGso();
        }
        if (_gsoSeg == 0)
        {
            _gsoSeg = len;
        }
        datagram.CopyTo(_gsoBuf.AsSpan(_gsoLen));
        _gsoLen += len;
        if (len < _gsoSeg)
        {
            _gsoClosed = true;
        }
    }

    private void FlushGso()
    {
        if (_gsoLen == 0)
        {
            return;
        }
        Send(_gsoBuf.AsSpan(0, _gsoLen), _gsoLen > _gsoSeg ? _gsoSeg : 0);
        _gsoLen = 0;
        _gsoSeg = 0;
        _gsoClosed = false;
    }

    // --- engine egress pump -------------------------------------------------------------------

    // Replay deferred stream bytes now that the window may have opened, then drain the engine's
    // own frames. Runs after every inbound datagram (ACKs open the window) and every timer.
    private void FlushEgress()
    {
        ReplayPending();
        FlushConnection();
    }

    private void ReplayPending()
    {
        while (!_closed)
        {
            if (_pendingData is null)
            {
                if (!_pendingSend.TryDequeue(out (long Sid, byte[] Data, bool Fin) next))
                {
                    return;
                }
                (_pendingSid, _pendingData, _pendingFin) = next;
                _pendingOff = 0;
            }

            int consumed = WriteStream(_pendingSid, _pendingData.AsSpan(_pendingOff), _pendingFin, out bool complete);
            _pendingOff   += consumed;
            _pendingBytes -= consumed;
            if (!complete)
            {
                return;   // engine still full - the next ACK/timer flush retries
            }
            _pendingBytes -= _pendingData.Length - _pendingOff;   // discarded tail of a reset stream
            _pendingData = null;
        }
    }

    // Write one stream's bytes into the engine, sending each produced datagram. Returns the bytes
    // accepted; complete = every byte and the fin were taken (or the stream is gone and the rest
    // has nowhere to go). Never discards silently - the caller keeps the remainder.
    private int WriteStream(long streamId, ReadOnlySpan<byte> data, bool fin, out bool complete)
    {
        complete = data.Length == 0 && !fin;
        int off = 0;
        while (!_closed && !complete)
        {
            long consumed;
            nint n;
            fixed (byte* dest = _sendBuf)
            fixed (byte* src = data)
            {
                byte* dataPtr = off < data.Length ? src + off : null;
                nuint dataLen = (nuint)(data.Length - off);
                n = Ngtcp2.iq_conn_write(_conn, dest, (nuint)_sendBuf.Length,
                    streamId, dataPtr, dataLen, fin ? 1 : 0, &consumed, NowNs());
            }

            int code = (int)n;
            if (code < 0)
            {
                if (code == Ngtcp2.NGTCP2_ERR_STREAM_SHUT_WR)
                {
                    complete = true;   // the fin is already registered - nothing more can be written
                    return off;
                }
                if (code == Ngtcp2.NGTCP2_ERR_STREAM_NOT_FOUND)
                {
                    complete = true;   // peer reset/closed the stream - the remainder has nowhere to go
                    return off;
                }
                if (code == Ngtcp2.NGTCP2_ERR_STREAM_DATA_BLOCKED)
                {
                    return off;        // stream-level flow control - retry on a later flush
                }
                CloseFromEngine(code);
                return off;
            }

            if (consumed > 0)
            {
                off += (int)consumed;
            }
            if (n > 0)
            {
                QueueSend(_sendBuf.AsSpan(0, (int)n));
            }

            if (off == data.Length && (data.Length > 0 || n > 0))
            {
                complete = true;   // all bytes accepted; the fin (if any) rode the final ones
                return off;
            }
            if (n == 0 && consumed <= 0)
            {
                return off;        // engine can't take more now (cwnd/pacing/amplification)
            }
        }
        return off;
    }

    // Drain ngtcp2's own frames (ACKs, handshake, CRYPTO, MAX_STREAMS) until it has nothing more.
    private void FlushConnection()
    {
        while (!_closed)
        {
            long consumed;
            nint n;
            fixed (byte* dest = _sendBuf)
            {
                n = Ngtcp2.iq_conn_write(_conn, dest, (nuint)_sendBuf.Length, -1, null, 0, 0, &consumed, NowNs());
            }
            if ((int)n < 0)
            {
                CloseFromEngine((int)n);
                return;
            }
            if (n == 0)
            {
                return;
            }
            QueueSend(_sendBuf.AsSpan(0, (int)n));
        }
    }

    // --- teardown -----------------------------------------------------------------------------

    private void CloseFromEngine(int liberr)
    {
        if (liberr != -Ngtcp2.NGTCP2_ERR_DRAINING && liberr != Ngtcp2.NGTCP2_ERR_DRAINING)
        {
            Console.Error.WriteLine($"[ioxide.quic] connection closed: {Ngtcp2.StrError(liberr)}");
        }

        // Close the send gate before the transport wakes the handler (QuicRemoveConnection ->
        // MarkClosed resumes it inline): a farewell SendStream must not pump an errored conn.
        _closed = true;
        _reactor.QuicRemoveConnection(this);   // stop routing every CID to us
        Destroy();
    }

    // Idempotent per resource: callers may gate the send path (_closed) before getting here.
    private void Destroy()
    {
        _closed = true;
        if (_conn != 0)
        {
            Ngtcp2.iq_conn_free(_conn);
            _conn = 0;
        }
        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }

    // --- unmanaged callbacks from the shim (reactor thread) -----------------------------------

    private static QuicEngineConnection From(void* user)
        => (QuicEngineConnection)GCHandle.FromIntPtr((nint)user).Target!;

    [UnmanagedCallersOnly]
    internal static void CbStreamData(void* user, long streamId, byte* data, nuint len, int fin)
        => From(user).OnStreamData(streamId, new ReadOnlySpan<byte>(data, (int)len), fin != 0);

    [UnmanagedCallersOnly]
    internal static void CbStreamClose(void* user, long streamId, ulong appError)
        => From(user).OnStreamClosed(streamId, appError);

    [UnmanagedCallersOnly]
    internal static void CbHandshakeCompleted(void* user)
    {
        // Established flips in OnDatagram too; this fires it precisely at the engine's signal.
        QuicEngineConnection c = From(user);
        if (!c._handshakeDone)
        {
            c.HandshakeCompletedOnce();
        }
    }

    [UnmanagedCallersOnly]
    internal static void CbNewCid(void* user, byte* cid, nuint len)
    {
        QuicEngineConnection c = From(user);
        c._reactor.QuicRegisterCid(c, new QuicCid(new ReadOnlySpan<byte>(cid, (int)len)));
    }

    [UnmanagedCallersOnly]
    internal static void CbRetireCid(void* user, byte* cid, nuint len)
    {
        QuicEngineConnection c = From(user);
        c._reactor.QuicUnregisterCid(new QuicCid(new ReadOnlySpan<byte>(cid, (int)len)));
    }
}
