using System.Diagnostics;
using System.Runtime.InteropServices;
using ioxide;

namespace ioxide.ngtcp2;

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
public unsafe partial class QuicEngineConnection : QuicConnection
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

    // Stream lifecycle -> the shared recv queue, ordered with the data that preceded it. All three
    // fire inside iq_conn_read/handle_expiry, so the once-per-read wake covers them.
    private void EnqueueLifecycle(long streamId, QuicStreamEvent kind, ulong appError)
    {
        if (EnqueueStreamEvent(streamId, kind, appError))
        {
            _recvEnqueued = true;
        }
        else
        {
            _recvOverflow = true;
        }
    }

    // The peer refuses our response (STOP_SENDING): stop feeding the stream. Retained chunks are
    // NOT freed here - ngtcp2 may still hold references until the stream closes.
    private void MarkOutStreamDead(long streamId)
    {
        if (_outStreams.TryGetValue(streamId, out OutStream? os))
        {
            os.Dead = true;
        }
    }

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

    // Teardown

    private void CloseFromEngine(int liberr)
    {
        // Draining and idle-close are normal lifecycle (the peer finished, or vanished and the
        // idle timer reaped the connection - every abandoned benchmark client ends this way);
        // only genuine protocol/engine errors are worth a log line.
        if (liberr != -Ngtcp2.NGTCP2_ERR_DRAINING && liberr != Ngtcp2.NGTCP2_ERR_DRAINING &&
            liberr != Ngtcp2.NGTCP2_ERR_IDLE_CLOSE)
        {
            Console.Error.WriteLine($"[ioxide.ngtcp2] connection closed: {Ngtcp2.StrError(liberr)}");
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
        foreach (OutStream os in _outStreams.Values)
        {
            while (os.Chunks.Count > 0)
            {
                (nint ptr, int len) = os.Chunks.Dequeue();
                NativeMemory.Free((void*)ptr);
                _outRetained -= len;
            }
        }
        _outStreams.Clear();
        _outPending.Clear();
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
}
