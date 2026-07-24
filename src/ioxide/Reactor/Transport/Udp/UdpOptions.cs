namespace ioxide;

/// <summary>The UDP side of <see cref="ServerConfig"/>: raw datagram sockets (QUIC binds its own
/// port through <see cref="ServerConfig.Quic"/> and only shares these tunables).</summary>
public sealed record UdpOptions
{
    /// <summary>
    /// UDP ports to bind (every reactor binds each one via SO_REUSEPORT, like the TCP listeners).
    /// Datagrams are delivered to <see cref="Reactor.OnDatagram"/> on the reactor thread. Empty
    /// (default) means no UDP sockets are opened.
    /// </summary>
    public ushort[] Ports { get; init; } = [];

    /// <summary>
    /// Depth of the shared UDP provided-buffer ring (rounded up to a power of two). One multishot
    /// RECVMSG per socket draws buffers from this ring; each buffer pins ~64 KiB (a full GRO train
    /// plus the packed address/control header) and returns as soon as its datagram is handled, so
    /// the depth bounds how many datagrams can be in flight across all UDP sockets at once.
    /// </summary>
    public int RecvSlots { get; init; } = 16;

    /// <summary>
    /// Enable UDP_GRO on receive: the kernel coalesces a burst of equal-size datagrams from one
    /// peer into a single completion, and <see cref="UdpDatagram.GroSegmentSize"/> carries the
    /// segment size for the handler to split on.
    /// </summary>
    public bool Gro { get; init; } = true;
}
