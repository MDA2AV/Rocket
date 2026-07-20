using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace ioxide;

/// <summary>
/// A QUIC connection ID (RFC 9000 §5.1: 0-20 bytes), packed into a fixed-size value type so it can
/// key the demux dictionary without allocation. Server-minted CIDs are random, so the default
/// (per-process-randomized) hash is collision-resistant against remote attackers.
/// </summary>
public readonly struct QuicCid : IEquatable<QuicCid>
{
    public const int MaxLength = 20;

    private readonly ulong _a;   // bytes 0-7   (zero-padded past Length)
    private readonly ulong _b;   // bytes 8-15
    private readonly uint  _c;   // bytes 16-19
    private readonly byte  _len;

    public int Length => _len;

    public QuicCid(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaxLength)
        {
            throw new ArgumentException($"connection id length {bytes.Length} exceeds {MaxLength}", nameof(bytes));
        }
        Span<byte> tmp = stackalloc byte[24];
        tmp.Clear();
        bytes.CopyTo(tmp);
        _a   = BinaryPrimitives.ReadUInt64LittleEndian(tmp);
        _b   = BinaryPrimitives.ReadUInt64LittleEndian(tmp[8..]);
        _c   = BinaryPrimitives.ReadUInt32LittleEndian(tmp[16..]);
        _len = (byte)bytes.Length;
    }

    public void CopyTo(Span<byte> destination)
    {
        Span<byte> tmp = stackalloc byte[24];
        BinaryPrimitives.WriteUInt64LittleEndian(tmp, _a);
        BinaryPrimitives.WriteUInt64LittleEndian(tmp[8..], _b);
        BinaryPrimitives.WriteUInt32LittleEndian(tmp[16..], _c);
        tmp[.._len].CopyTo(destination);
    }

    public bool Equals(QuicCid other) => _a == other._a && _b == other._b && _c == other._c && _len == other._len;
    public override bool Equals(object? obj) => obj is QuicCid other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(_a, _b, _c, _len);
    public override string ToString() => $"cid[{_len}]{_a:x16}{_b:x16}{_c:x8}";
}

/// <summary>Why the transport dropped a connection it was tracking.</summary>
public enum QuicEvictReason
{
    IdleTimeout,
    ReactorShutdown,
}

/// <summary>
/// A logical QUIC connection tracked by the transport's CID demux. The QUIC engine binding
/// (ngtcp2/quicly - the sans-I/O protocol state machine) subclasses this: datagrams routed by
/// DCID arrive via <see cref="OnDatagram"/>, replies leave via <see cref="Send"/>, and the timer
/// sweep drives loss/handshake deadlines. All members run on the owning reactor thread.
/// </summary>
public abstract class QuicConnection
{
    public Reactor Reactor { get; internal set; } = null!;
    public int SocketFd { get; internal set; }

    // Peer sockaddr snapshot, transport-owned native memory (freed on eviction). Updated only via
    // UpdatePeerAddress - the engine decides when a migration is validated, not the transport.
    internal nint PeerAddr;
    internal int  PeerAddrLen;

    internal readonly List<QuicCid> Cids = [];
    internal long LastSeenMs;

    /// <summary>
    /// One UDP payload for this connection (with GRO, a train of <paramref name="groSegmentSize"/>-
    /// sized datagrams to split before feeding the engine). Spans are valid only during the call.
    /// </summary>
    public abstract void OnDatagram(ReadOnlySpan<byte> payload, byte tos, int groSegmentSize);

    /// <summary>Next engine deadline in <see cref="Environment.TickCount64"/> ms; long.MaxValue = none.</summary>
    public abstract long GetNextTimeout(long nowMs);

    /// <summary>Deadline passed - run loss/handshake/idle processing and flush whatever it produced.</summary>
    public abstract void OnTimer(long nowMs);

    /// <summary>The transport dropped this connection (it is already unregistered when this runs).</summary>
    public abstract void OnEvicted(QuicEvictReason reason);

    /// <summary>Send one datagram (or a GSO batch) to the connection's current peer address.</summary>
    protected void Send(ReadOnlySpan<byte> payload, int gsoSegmentSize = 0)
        => Reactor.UdpSendTo(SocketFd, PeerAddr, PeerAddrLen, payload, gsoSegmentSize);

    /// <summary>Adopt a validated peer migration (copies the sockaddr out of the datagram).</summary>
    public unsafe void UpdatePeerAddress(nint addr, int addrLen)
    {
        Buffer.MemoryCopy((void*)addr, (void*)PeerAddr, Reactor.UdpNameCap, addrLen);
        PeerAddrLen = addrLen;
    }
}

/// <summary>
/// Invoked on the reactor thread for a long-header packet whose DCID is unknown - i.e. a new
/// connection attempt. Return the engine-backed connection to adopt it, or null to drop the packet.
/// </summary>
public delegate QuicConnection? QuicConnectionFactory(Reactor reactor, in UdpDatagram datagram, in QuicCid dcid);

public sealed record QuicOptions
{
    public ushort Port { get; init; } = 443;

    /// <summary>
    /// Length of the CIDs this endpoint mints. Short-header packets carry no CID length on the
    /// wire, so the demux slices exactly this many bytes - every locally-issued CID must use it.
    /// </summary>
    public int LocalCidLength { get; init; } = 8;

    public QuicConnectionFactory? ConnectionFactory { get; init; }

    /// <summary>
    /// Transport-level backstop for connections whose engine went quiet (the engine's own
    /// idle_timeout is the real mechanism). 0 disables the sweep eviction.
    /// </summary>
    public int IdleTimeoutMs { get; init; } = 60_000;
}

/// <summary>
/// QUIC transport: rides the UDP layer (Reactor.Udp.cs) on one dedicated port and demultiplexes
/// datagrams to logical connections by Destination Connection ID (RFC 8999 version-independent
/// parse), since one UDP socket carries every connection - the fd-keyed TCP table cannot model
/// this. Packet protection and the handshake live in the engine subclass of
/// <see cref="QuicConnection"/>, produced by <see cref="QuicOptions.ConnectionFactory"/>; the
/// engine registers the CIDs it mints via <see cref="QuicRegisterCid"/> as the handshake retires
/// the client's initial DCID. Timer deadlines ride the reactor ticker (250 ms granularity - fine
/// for handshake/idle deadlines; a finer loss-timer source can follow with the engine binding).
/// </summary>
public sealed unsafe partial class Reactor
{
    private QuicOptions? _quic;
    private readonly Dictionary<QuicCid, QuicConnection> _quicConns = new();
    private readonly HashSet<QuicConnection> _quicConnSet = [];
    private readonly List<QuicConnection> _quicSweepScratch = [];

    private void InitQuic(QuicOptions options)
    {
        _quic = options;
        AddTicker(QuicSweep);
    }

    private void QuicDispatch(in UdpDatagram datagram)
    {
        if (!TryExtractDcid(datagram.Payload, _quic!.LocalCidLength, out QuicCid dcid, out bool longHeader))
        {
            return;   // not parseable as QUIC - drop
        }

        if (_quicConns.TryGetValue(dcid, out QuicConnection? conn))
        {
            conn.LastSeenMs = Environment.TickCount64;
            conn.OnDatagram(datagram.Payload, datagram.Tos, datagram.GroSegmentSize);
            return;
        }

        if (!longHeader)
        {
            return;   // short header for an unknown CID: stale/garbage (stateless reset later)
        }

        QuicConnection? fresh = _quic.ConnectionFactory?.Invoke(this, in datagram, in dcid);
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

        fresh.OnDatagram(datagram.Payload, datagram.Tos, datagram.GroSegmentSize);
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
        _quicConnSet.Remove(conn);

        if (conn.PeerAddr != 0)
        {
            NativeMemory.Free((void*)conn.PeerAddr);
            conn.PeerAddr = 0;
        }
    }

    // Ticker callback (~250 ms): fire engine deadlines, evict quiet connections.
    private void QuicSweep()
    {
        long now = Environment.TickCount64;
        int idleMs = _quic!.IdleTimeoutMs;

        _quicSweepScratch.Clear();
        _quicSweepScratch.AddRange(_quicConnSet);

        foreach (QuicConnection conn in _quicSweepScratch)
        {
            if (idleMs > 0 && now - conn.LastSeenMs > idleMs)
            {
                QuicRemoveConnection(conn);
                conn.OnEvicted(QuicEvictReason.IdleTimeout);
                continue;
            }

            if (conn.GetNextTimeout(now) <= now)
            {
                conn.OnTimer(now);
            }
        }
    }

    private void TeardownQuic()
    {
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
