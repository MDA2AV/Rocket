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
            _handshakeDone = true;
            OnHandshakeCompleted();
        }

        FlushEgress();
        FireRecv();
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
        // The sweep works in TickCount64 ms; convert the ns deadline to that clock's frame.
        long deltaMs = (long)((expiryNs - NowNs()) / 1_000_000);
        return nowMs + Math.Max(0, deltaMs);
    }

    public override void OnTimer(long nowMs)
    {
        if (_closed)
        {
            return;
        }
        int rv = Ngtcp2.iq_conn_handle_expiry(_conn, NowNs());
        if (rv != 0)
        {
            CloseFromEngine(rv);
            return;
        }
        FlushEgress();
        FireRecv();
    }

    public override void OnEvicted(QuicEvictReason reason)
    {
        Destroy();
    }

    // --- application-facing send --------------------------------------------------------------

    /// <summary>Queue bytes on a stream and flush. streamId must come from a delivered item or
    /// <see cref="OpenUniStream"/>. fin closes the send side.</summary>
    public override void SendStream(long streamId, ReadOnlySpan<byte> data, bool fin)
    {
        if (_closed)
        {
            return;
        }
        Pump(streamId, data, fin);
    }

    /// <summary>Open a server-initiated unidirectional stream; returns its id, or -1 on failure.</summary>
    protected long OpenUniStream()
    {
        // Reserved for the H3 control/QPACK streams; wired with ngtcp2_conn_open_uni_stream when
        // the H3 layer lands. Not needed for the bidi echo milestone.
        return -1;
    }

    // --- engine egress pump -------------------------------------------------------------------

    // Drain ngtcp2's non-stream output (ACKs, handshake, CRYPTO) until it has nothing more.
    private void FlushEgress() => Pump(-1, default, false);

    // Write one stream's data, then keep pumping the engine's remaining datagrams. streamId == -1
    // means "no stream data, just flush". Handles ngtcp2's writev_stream loop: advance by the
    // consumed count, and once the stream can take no more (all sent / FIN'd / blocked) fall back
    // to stream -1 so coalesced ACKs and the FIN's own packets still go out.
    private void Pump(long streamId, ReadOnlySpan<byte> data, bool fin)
    {
        if (_closed)
        {
            return;
        }
        int off = 0;
        while (true)
        {
            long consumed;
            nint n;
            fixed (byte* dest = _sendBuf)
            fixed (byte* src = data)
            {
                byte* dataPtr = streamId >= 0 ? src + off : null;
                nuint dataLen = streamId >= 0 ? (nuint)(data.Length - off) : 0;
                n = Ngtcp2.iq_conn_write(_conn, dest, (nuint)_sendBuf.Length,
                    streamId, dataPtr, dataLen, fin ? 1 : 0, &consumed, NowNs());
            }

            int code = (int)n;
            if (code < 0)
            {
                // Stream can't accept more right now - stop feeding it, but keep flushing the
                // connection's own packets (ACKs, the FIN we already handed over) via stream -1.
                if (code is Ngtcp2.NGTCP2_ERR_STREAM_DATA_BLOCKED
                         or Ngtcp2.NGTCP2_ERR_STREAM_SHUT_WR
                         or Ngtcp2.NGTCP2_ERR_STREAM_NOT_FOUND)
                {
                    streamId = -1;
                    continue;
                }
                CloseFromEngine(code);
                return;
            }

            if (consumed > 0)
            {
                off += (int)consumed;
            }
            if (n > 0)
            {
                Send(_sendBuf.AsSpan(0, (int)n));
            }

            if (n == 0)
            {
                // Nothing more to write on this stream. If stream data remains unsent, drop to the
                // generic flush; otherwise we are done.
                if (streamId >= 0 && off < data.Length)
                {
                    streamId = -1;
                    continue;
                }
                return;
            }
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
            c._handshakeDone = true;
            c.OnHandshakeCompleted();
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
