namespace ioxide;

public sealed record QuicOptions
{
    public ushort Port { get; init; } = 443;

    /// <summary>
    /// Length of the CIDs this endpoint mints. Short-header packets carry no CID length on the
    /// wire, so the demux slices exactly this many bytes - every locally-issued CID must use it.
    /// </summary>
    public int LocalCidLength { get; init; } = 8;

    /// <summary>
    /// How a datagram reaches the reactor that owns its connection when several reactors share the
    /// port. Defaults to <see cref="QuicRouting.Forward"/>, which costs nothing until a client
    /// changes address; see <see cref="QuicRouting"/> for the measured trade.
    /// </summary>
    public QuicRouting Routing { get; init; } = QuicRouting.Forward;

    /// <summary>
    /// Under <see cref="QuicRouting.Forward"/>, claim a migrated client's new address with a socket
    /// of the owning reactor's own, so the kernel delivers there directly and the forwarding stops
    /// after the first datagram or two. Costs one file descriptor per migrated connection.
    /// </summary>
    public bool PinMigratedPeers { get; init; } = true;

    public QuicConnectionFactory? ConnectionFactory { get; init; }

    /// <summary>
    /// Transport-level backstop for connections whose engine went quiet (the engine's own
    /// idle_timeout is the real mechanism). 0 disables the sweep eviction.
    /// </summary>
    public int IdleTimeoutMs { get; init; } = 60_000;
}

/// <summary>
/// Invoked on the reactor thread for a long-header packet whose DCID is unknown - i.e. a new
/// connection attempt. Return the engine-backed connection to adopt it, or null to drop the packet.
/// </summary>
public delegate QuicConnection? QuicConnectionFactory(Reactor reactor, in UdpDatagram datagram, in QuicCid dcid);
