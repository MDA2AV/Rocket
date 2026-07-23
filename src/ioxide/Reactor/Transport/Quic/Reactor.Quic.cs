using System.Runtime.InteropServices;

namespace ioxide;

/// <summary>
/// QUIC transport: rides the UDP layer (Reactor.Udp.cs) on one dedicated port and demultiplexes
/// datagrams to logical connections by Destination TcpConnection ID (RFC 8999 version-independent
/// parse), since one UDP socket carries every connection - the fd-keyed TCP table cannot model
/// this. Packet protection and the handshake live in the engine subclass of
/// <see cref="QuicConnection"/>, produced by <see cref="QuicOptions.ConnectionFactory"/>; the
/// engine registers the CIDs it mints via <see cref="QuicRegisterCid"/> as the handshake retires
/// the client's initial DCID. Timer deadlines ride the reactor ticker (250 ms granularity - fine
/// for handshake/idle deadlines; a finer loss-timer source can follow with the engine binding).
/// </summary>
public sealed unsafe partial class Reactor
{
    private QuicOptions? _quicOptions;
    private readonly Dictionary<QuicCid, QuicConnection> _quicConns = new();
    private readonly HashSet<QuicConnection> _quicConnSet = [];
    private readonly List<QuicConnection> _quicSweepScratch = [];

    // No-op unless ServerConfig.Quic is set (the port itself is bound by OpenUdpSockets).
    private void InitQuic()
    {
        if (_config.Quic is not { } options)
        {
            return;
        }
        _quicOptions = options;
        AddTicker(QuicSweep);
    }

    private void QuicDispatch(in UdpDatagram datagram)
    {
        // A GRO train can interleave datagrams of DIFFERENT connections - they share the client's
        // 4-tuple, so the kernel coalesces across them. Demux per segment: routing the whole train
        // by its first packet's DCID would feed other connections' packets to the wrong engine
        // (which silently drops them - the peers just stall).
        if (datagram.GroSegmentSize > 0 && datagram.GroSegmentSize < datagram.Payload.Length)
        {
            int stride = datagram.GroSegmentSize;
            for (int off = 0; off < datagram.Payload.Length; off += stride)
            {
                int len = Math.Min(stride, datagram.Payload.Length - off);
                QuicDispatchDatagram(new UdpDatagram(datagram.SocketFd, datagram.LocalPort,
                    datagram.PeerAddr, datagram.PeerAddrLen,
                    datagram.Payload.Slice(off, len), 0, datagram.Tos));
            }
            return;
        }

        QuicDispatchDatagram(in datagram);
    }

    private void QuicDispatchDatagram(in UdpDatagram datagram)
    {
        // Reads only the cleartext prefix per RFC 8999
        if (!TryExtractDcid(datagram.Payload, _quicOptions!.LocalCidLength, out QuicCid dcid, out bool longHeader))
        {
            return;   // not parseable as QUIC - drop
        }

        // Quick connection lookup
        if (_quicConns.TryGetValue(dcid, out QuicConnection? conn))
        {
            conn.LastSeenMs = Environment.TickCount64;
            conn.OnDatagram(datagram.Payload, datagram.Tos, datagram.GroSegmentSize);
            QuicArmTimer(conn);   // reads/handler sends (inline above) moved the engine deadline
            return;
        }

        if (!longHeader)
        {
            return;   // short header for an unknown CID: stale/garbage (stateless reset later)
        }

        QuicConnection? fresh = _quicOptions.ConnectionFactory?.Invoke(this, in datagram, in dcid);
        if (fresh == null)
        {
            return;
        }

        fresh.Reactor     = this;
        fresh.SocketFd    = datagram.SocketFd;
        fresh.PeerAddr    = (nint)NativeMemory.Alloc(UdpNameCap);
        fresh.PeerAddrLen = datagram.PeerAddrLen;
        Buffer.MemoryCopy((void*)datagram.PeerAddr, (void*)fresh.PeerAddr, UdpNameCap, datagram.PeerAddrLen);
        fresh.LastSeenMs = Environment.TickCount64;

        fresh.Cids.Add(dcid);
        _quicConns[dcid] = fresh;
        _quicConnSet.Add(fresh);

        // Two owners: this transport (released in QuicRemoveConnection) and the handler. The
        // handler launches before the first datagram is fed - if the engine delivers stream data
        // right away, the sticky pending flag completes the handler's first ReadAsync.
        fresh.InitRefs();
        if (QuicHandle is not null)
        {
            _ = RunQuicHandlerAsync(fresh);
        }
        else
        {
            fresh.DecRef();   // no handler configured: the transport stays the only owner
        }

        fresh.OnDatagram(datagram.Payload, datagram.Tos, datagram.GroSegmentSize);
        QuicArmTimer(fresh);
    }

    // RFC 8999 (version-independent invariants): long header (bit 0x80) carries an explicit DCID
    // length at offset 5; short header carries the DCID bare at offset 1 with its length known
    // only to the endpoint that minted it (shortCidLen).
    internal static bool TryExtractDcid(ReadOnlySpan<byte> packet, int shortCidLen, out QuicCid dcid, out bool longHeader)
    {
        dcid = default;
        longHeader = false;

        if (packet.IsEmpty)
        {
            return false;
        }

        if ((packet[0] & 0x80) != 0)
        {
            longHeader = true;
            if (packet.Length < 6)
            {
                return false;
            }
            int len = packet[5];
            if (len > QuicCid.MaxLength || packet.Length < 6 + len)
            {
                return false;   // >20 is legal on the wire but never one of ours - not routable
            }
            dcid = new QuicCid(packet.Slice(6, len));
            return true;
        }

        if (packet.Length < 1 + shortCidLen)
        {
            return false;
        }
        dcid = new QuicCid(packet.Slice(1, shortCidLen));
        return true;
    }

    /// <summary>
    /// Route future datagrams carrying <paramref name="cid"/> to <paramref name="conn"/>. The
    /// engine calls this for every CID it issues (NEW_CONNECTION_ID, handshake SCID). Reactor
    /// thread only.
    /// </summary>
    public void QuicRegisterCid(QuicConnection conn, in QuicCid cid)
    {
        _quicConns[cid] = conn;
        conn.Cids.Add(cid);
    }

    /// <summary>Stop routing <paramref name="cid"/> (RETIRE_CONNECTION_ID). Reactor thread only.</summary>
    public void QuicUnregisterCid(in QuicCid cid)
    {
        if (_quicConns.Remove(cid, out QuicConnection? conn))
        {
            conn.Cids.Remove(cid);
        }
    }

    /// <summary>
    /// Drop a connection entirely: every CID route, the sweep membership, and the transport-owned
    /// peer-address memory. The engine calls this once its CONNECTION_CLOSE/drain completes.
    /// Reactor thread only.
    /// </summary>
    public void QuicRemoveConnection(QuicConnection conn)
    {
        foreach (QuicCid cid in conn.Cids)
        {
            _quicConns.Remove(cid);
        }
        conn.Cids.Clear();

        // The set membership doubles as the "transport still owns a ref" flag, so a second call
        // (engine close racing the idle sweep) cannot double-release.
        if (_quicConnSet.Remove(conn))
        {
            // Wake the handler with closed=1 first - it resumes inline, sees IsClosed, and releases
            // its own ref - then invalidate any awaiter that could outlive this life.
            conn.MarkClosed();
            conn.BumpGeneration();

            if (conn.PeerAddr != 0)
            {
                NativeMemory.Free((void*)conn.PeerAddr);
                conn.PeerAddr = 0;
            }

            conn.DecRef();
        }
    }

    // Ticker callback (~250 ms): evict quiet connections. Engine deadlines are fired by
    // QuicFireDueTimers at loop-pass granularity; this ticker's loop wake doubles as its floor.
    private void QuicSweep()
    {
        long now = Environment.TickCount64;
        int idleMs = _quicOptions!.IdleTimeoutMs;

        _quicSweepScratch.Clear();
        _quicSweepScratch.AddRange(_quicConnSet);

        foreach (QuicConnection conn in _quicSweepScratch)
        {
            if (idleMs > 0 && now - conn.LastSeenMs > idleMs)
            {
                QuicRemoveConnection(conn);
                conn.OnEvicted(QuicEvictReason.IdleTimeout);
            }
        }
    }

    // Earliest engine deadline across live conns; long.MaxValue = none. Checked at the top of every
    // loop pass, so loss/PTO timers fire at completion-batch granularity (~RTT under load) instead
    // of the 250 ms ticker - a retransmit that waits 250 ms per loss makes storms self-sustaining.
    private long _quicNextTimeoutMs = long.MaxValue;

    private void QuicFireDueTimers()
    {
        if (_quicConnSet.Count == 0 || Environment.TickCount64 < _quicNextTimeoutMs)
        {
            return;
        }

        long now = Environment.TickCount64;
        long next = long.MaxValue;
        _quicSweepScratch.Clear();
        _quicSweepScratch.AddRange(_quicConnSet);
        foreach (QuicConnection conn in _quicSweepScratch)
        {
            long deadline = conn.GetNextTimeout(now);
            if (deadline <= now)
            {
                conn.OnTimer(now);
                deadline = conn.GetNextTimeout(now);
            }
            if (deadline < next)
            {
                next = deadline;
            }
        }
        _quicNextTimeoutMs = next;
    }

    // Pull the tracked minimum forward after engine activity on a conn (its expiry may now be the
    // earliest). Deadlines that move LATER are caught by the next full scan when the stale minimum
    // fires - one wasted scan, never a missed timer.
    private void QuicArmTimer(QuicConnection conn)
    {
        long deadline = conn.GetNextTimeout(Environment.TickCount64);
        if (deadline < _quicNextTimeoutMs)
        {
            _quicNextTimeoutMs = deadline;
        }
    }

    private void TeardownQuic()
    {
        if (_quicOptions == null)
        {
            return;
        }
        _quicSweepScratch.Clear();
        _quicSweepScratch.AddRange(_quicConnSet);
        foreach (QuicConnection conn in _quicSweepScratch)
        {
            QuicRemoveConnection(conn);
            conn.OnEvicted(QuicEvictReason.ReactorShutdown);
        }
        _quicConns.Clear();
    }
}
