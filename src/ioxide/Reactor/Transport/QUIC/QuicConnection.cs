namespace ioxide;

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