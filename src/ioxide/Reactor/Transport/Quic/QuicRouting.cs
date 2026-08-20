namespace ioxide;

/// <summary>
/// How a QUIC datagram reaches the reactor that owns its connection when there is more than one.
/// Both settings solve the same problem: the kernel picks a reactor by hashing the sender's
/// address, which stops being right the moment that address changes. See Reactor.Quic.Forward.cs.
///
/// The choice is who pays. Measured on one machine, two reactors, h3 benchmark - treat the
/// magnitudes as indicative:
///
/// <list type="table">
///   <listheader><term>setting</term><description>never migrates / migrates</description></listheader>
///   <item><term><see cref="Forward"/></term><description>nothing / ~8.5 us per datagram</description></item>
///   <item><term><see cref="KernelFilter"/></term><description>free with headroom, ~-12% at saturation / nothing</description></item>
/// </list>
///
/// Unless a large share of clients migrate, <see cref="Forward"/> is cheaper in aggregate, which is
/// why it is the default.
/// </summary>
public enum QuicRouting
{
    /// <summary>
    /// Hash as before, and hand a misdirected datagram to its owner over the reactor post queue.
    /// No privileges, and reactor startup is unchanged.
    /// </summary>
    Forward = 0,

    /// <summary>
    /// Additionally attach a classic-BPF program to the SO_REUSEPORT group, so the kernel routes by
    /// connection id and a migrated client's datagrams arrive at their owner directly.
    ///
    /// Reactors must then open their UDP sockets in ShardIndex order, since the program answers
    /// with a position in the group and that is bind order - a startup-only rendezvous. Best-effort:
    /// if the kernel refuses the program, <see cref="Forward"/> remains underneath. Correctness
    /// never depends on the filter, only cost does.
    /// </summary>
    KernelFilter = 1,
}
