namespace ioxide;

/// <summary>
/// How a QUIC datagram reaches the reactor that owns its connection, when the fleet has more than
/// one.
///
/// The problem both settings solve: every reactor binds the QUIC port with SO_REUSEPORT and the
/// kernel chooses between them by hashing the sender's address, which stops being the right answer
/// the moment a client's address changes. See Reactor.Quic.Forward.cs.
///
/// Measured on one machine with two reactors and the h3 benchmark, so treat the magnitudes as
/// indicative and the shape as real:
///
/// <list type="table">
///   <listheader><term>setting</term><description>connections that never migrate / that do</description></listheader>
///   <item><term><see cref="Forward"/></term><description>no cost at all / about 8.5 us per datagram</description></item>
///   <item><term><see cref="KernelFilter"/></term><description>free with CPU headroom, about -12% throughput at saturation / no cost</description></item>
/// </list>
///
/// So the choice is who pays. <see cref="Forward"/> charges only the connections that actually
/// migrate, and charges them a cross-thread wake per datagram. <see cref="KernelFilter"/> charges
/// every connection a little kernel work per packet - invisible while there is CPU to spare, and
/// real once there is not - and charges migrating ones nothing. Unless a large share of clients
/// migrate, <see cref="Forward"/> is cheaper in aggregate, which is why it is the default.
/// </summary>
public enum QuicRouting
{
    /// <summary>
    /// Let the kernel hash as it does today, and hand a misdirected datagram to its owner over the
    /// reactor post queue. Costs nothing until a client actually moves, needs no privileges, and
    /// leaves reactor startup exactly as it is.
    /// </summary>
    Forward = 0,

    /// <summary>
    /// Additionally attach a classic-BPF program to the port's SO_REUSEPORT group so the kernel
    /// routes by connection id rather than by address, and a migrated client's datagrams arrive at
    /// their owner directly.
    ///
    /// Two consequences worth knowing. Reactors must then open their UDP sockets in ShardIndex
    /// order, because the program answers with a position in the reuseport group and that position
    /// is bind order - a startup-only rendezvous that does not exist otherwise. And the filter is
    /// best-effort: if the kernel refuses it (an old kernel, a seccomp policy, a restricted
    /// container) ioxide says so and carries on, with <see cref="Forward"/> still underneath as the
    /// backstop. Correctness never depends on the filter; only the cost does.
    /// </summary>
    KernelFilter = 1,
}
