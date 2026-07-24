namespace ioxide;

/// <summary>
/// All server tunables; override via object initializer. Engine-wide knobs live here; everything
/// transport-specific is grouped: <see cref="Tcp"/> (listeners, buffer rings, write path),
/// <see cref="Udp"/> (raw datagram sockets), <see cref="Quic"/> (the QUIC transport, which binds
/// its own UDP port).
/// </summary>
public sealed record ServerConfig
{
    public int ReactorCount { get; init; } = 12;

    // io_uring SQ/CQ depth.
    public uint RingEntries { get; init; } = 8192;

    /// <summary>
    /// Bind listeners as dual-stack IPv6 (AF_INET6 on :: with IPV6_V6ONLY=0) so one socket accepts both
    /// IPv6 and IPv4-mapped clients. When false (default) listeners are IPv4-only (AF_INET on 0.0.0.0).
    /// Applies to TCP listeners and UDP sockets alike.
    /// </summary>
    public bool DualStack { get; init; } = false;

    // --- reactor recv machinery: io_uring provided-buffer rings, registered per reactor at
    //     startup (TCP recv draws on them; UDP has its own ring, sized by UdpOptions.RecvSlots). ---

    // Shared buffer ring (Incremental == false).
    public int RecvBufferSize    { get; init; } = 32 * 1024;
    public int BufferRingEntries { get; init; } = 4096;

    // Incremental mode (IOU_PBUF_RING_INC, kernel 6.12+): per-connection buffer rings, and the
    // reactor runs its incremental loop variant. Reserved native memory ≈
    // MaxConnections × ConnBufRingEntries × IncRecvBufferSize × ReactorCount.
    public bool Incremental        { get; init; } = false;
    public int  MaxConnections     { get; init; } = 4096;   // one bgid per active connection
    public int  ConnBufRingEntries { get; init; } = 16;
    public int  IncRecvBufferSize  { get; init; } = 4096;

    /// <summary>The TCP transport: listeners, connection pool, and the write path.</summary>
    public TcpOptions Tcp { get; init; } = new();

    /// <summary>Raw UDP sockets (datagrams reach <see cref="Reactor.OnDatagram"/>).</summary>
    public UdpOptions Udp { get; init; } = new();

    /// <summary>
    /// Enable the QUIC transport (Reactor.Quic.cs). Its port is bound as a UDP socket
    /// automatically (no need to repeat it in <see cref="UdpOptions.Ports"/>); datagrams on that
    /// port are demultiplexed by connection ID instead of reaching <see cref="Reactor.OnDatagram"/>.
    /// </summary>
    public QuicOptions? Quic { get; init; }
}
