namespace ioxide;

/// <summary>
/// One received datagram (or, with GRO, a coalesced train of equal-size datagrams from one peer),
/// delivered inline on the reactor thread. Payload and peer address point into the recv slot's
/// native block and are valid only for the duration of the handler call - copy what must outlive it.
/// </summary>
public readonly ref struct UdpDatagram
{
    /// <summary>The UDP socket the datagram arrived on (pass back to <see cref="Reactor.UdpSendTo"/> to reply).</summary>
    public readonly int SocketFd;

    /// <summary>The local port this socket is bound to (one handler can serve several ports).</summary>
    public readonly ushort LocalPort;

    /// <summary>Native sockaddr of the sender (sockaddr_in or sockaddr_in6 by family).</summary>
    public readonly nint PeerAddr;
    public readonly int  PeerAddrLen;

    public readonly ReadOnlySpan<byte> Payload;

    /// <summary>
    /// Non-zero when GRO coalesced a train: Payload holds consecutive datagrams of this size
    /// (the final one may be shorter). Zero means Payload is a single datagram.
    /// </summary>
    public readonly int GroSegmentSize;

    /// <summary>TOS/TCLASS byte of the datagram; the ECN codepoint is the low two bits.</summary>
    public readonly byte Tos;

    internal UdpDatagram(int socketFd, ushort localPort, nint peerAddr, int peerAddrLen,
        ReadOnlySpan<byte> payload, int groSegmentSize, byte tos)
    {
        SocketFd       = socketFd;
        LocalPort      = localPort;
        PeerAddr       = peerAddr;
        PeerAddrLen    = peerAddrLen;
        Payload        = payload;
        GroSegmentSize = groSegmentSize;
        Tos            = tos;
    }
}