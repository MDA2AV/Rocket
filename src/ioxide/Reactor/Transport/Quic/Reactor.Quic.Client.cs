namespace ioxide;

/// <summary>
/// Client-side QUIC: connections this reactor OPENS, as opposed to the ones it accepts. They share
/// the reactor's QUIC UDP socket, so the peer's replies arrive through the same multishot recv and
/// route through the same DCID demux - a client connection is just one more entry in the
/// connection table, and every later datagram, timer and eviction path treats it identically.
///
/// The engine binding (ioxide.ngtcp2) creates the connection and hands it here to adopt. The one
/// requirement that makes the routing work: the connection ID the client asks the peer to address
/// it by must be exactly <see cref="QuicLocalCidLength"/> bytes, because short-header packets carry
/// no CID length on the wire and the demux slices that many.
/// </summary>
public sealed unsafe partial class Reactor
{
    /// <summary>The reactor's QUIC UDP socket, or -1 when QUIC isn't configured. Client
    /// connections send from it and receive their replies on it.</summary>
    public int QuicSocketFd
    {
        get
        {
            if (_quicOptions is null)
            {
                return -1;
            }
            int index = Array.IndexOf(_udpFdPorts, _quicOptions.Port);
            return index < 0 ? -1 : _udpFds[index];
        }
    }

    /// <summary>The bound QUIC port (the local side of client connections).</summary>
    public ushort QuicLocalPort => _quicOptions?.Port ?? 0;

    /// <summary>CID length this reactor's demux slices - a client's own CID must match it.</summary>
    public int QuicLocalCidLength => _quicOptions?.LocalCidLength ?? 0;

    /// <summary>
    /// Adopt an outgoing connection the engine binding just created: route its replies by
    /// <paramref name="localCid"/>, join the timer/eviction sweeps, and take the transport's
    /// reference. Reactor thread only.
    /// </summary>
    /// <param name="connection">The engine-backed connection, already constructed.</param>
    /// <param name="localCid">The CID the peer will address us by; must be QuicLocalCidLength bytes.</param>
    /// <param name="peerAddr">Native sockaddr of the peer, owned by the connection from here on.</param>
    /// <param name="peerAddrLen">Its length.</param>
    public void QuicAdoptClient(QuicConnection connection, ReadOnlySpan<byte> localCid,
        nint peerAddr, int peerAddrLen)
    {
        if (_quicOptions is null)
        {
            throw new InvalidOperationException(
                "QUIC client connections need ServerConfig.Quic set - they ride the reactor's QUIC socket.");
        }
        if (localCid.Length != _quicOptions.LocalCidLength)
        {
            throw new ArgumentException(
                $"client CID must be {_quicOptions.LocalCidLength} bytes to match this reactor's demux, got {localCid.Length}.",
                nameof(localCid));
        }

        int socketFd = QuicSocketFd;
        if (socketFd < 0)
        {
            throw new InvalidOperationException("the QUIC UDP socket is not open on this reactor");
        }

        connection.Reactor     = this;
        connection.SocketFd    = socketFd;
        connection.PeerAddr    = peerAddr;
        connection.PeerAddrLen = peerAddrLen;
        connection.LastSeenMs  = Environment.TickCount64;

        var cid = new QuicCid(localCid);
        connection.Cids.Add(cid);
        _quicConns[cid] = connection;
        _quicConnSet.Add(connection);

        // Two owners, like an accepted connection: this transport, and the caller that opened it.
        connection.InitRefs();
        QuicArmTimer(connection);
    }
}
