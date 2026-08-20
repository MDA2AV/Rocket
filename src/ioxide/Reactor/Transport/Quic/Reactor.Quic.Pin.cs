using static ioxide.Native;

namespace ioxide;

/// <summary>
/// The optimisation on top of cross-reactor forwarding: once a migrated client's new address is
/// known, claim it with a socket of this reactor's own so the kernel delivers there directly and
/// the forwarding stops.
///
/// Forwarding alone is correct but permanent. The kernel picks a socket by hashing the sender's
/// address, and after a migration that address does not change back - so every one of that
/// client's datagrams keeps landing on the wrong reactor and keeps paying a hop, for the life of
/// the connection. See Reactor.Quic.Forward.cs.
///
/// A datagram socket bound to the same address as the others but <c>connect()</c>ed to one peer is
/// a MORE SPECIFIC match than a wildcard bind, and the kernel's lookup takes the narrowest match
/// before it ever reaches the reuseport hash. So the owning reactor opens one of those toward the
/// peer's new address, and from the next datagram on it arrives here directly.
///
/// Three things about that, each verified rather than assumed:
///
/// <list type="bullet">
/// <item><c>connect()</c> on a datagram socket puts nothing on the wire. It is a local declaration
/// of intent - no handshake, and the peer is never told.</item>
/// <item>Adding it does NOT re-scatter anybody else. A connected socket does not take part in
/// reuseport selection, so the group stays the size it was and every other peer keeps landing
/// exactly where it did. Measured: eight established peers, none moved.</item>
/// <item>It cannot bootstrap itself. This reactor only learns the new address from a datagram, and
/// the datagrams are going elsewhere - so forwarding has to deliver the first one. The two are
/// complements: forwarding makes the pin possible, the pin stops forwarding being forever.</item>
/// </list>
/// </summary>
public sealed unsafe partial class Reactor
{
    /// <summary>
    /// Ceiling on pinned sockets held at once, because each costs a file descriptor and ngtcp2
    /// adopts a new path BEFORE it finishes validating it - so a forged datagram can reach here.
    /// Past the ceiling a migrated connection simply keeps forwarding, which is slower and still
    /// correct. That is the right way round: the cheap failure is the safe one.
    /// </summary>
    private const int QuicMaxPinnedPeers = 512;

    private int  _quicServingFd = -1;   // the wildcard QUIC socket; pins are only made against it
    private int  _quicPinsOpen;
    private long _quicPinsCreated;

    /// <summary>Peers currently claimed by a socket of this reactor's own.</summary>
    public int QuicPinsOpen => Volatile.Read(ref _quicPinsOpen);

    /// <summary>Pins this reactor has opened since it started - one per migration it kept up with.</summary>
    public long QuicPinsCreated => Volatile.Read(ref _quicPinsCreated);

    /// <summary>
    /// Claim <paramref name="conn"/>'s current peer address, replacing any earlier claim. Called
    /// from the reactor's sweep, NOT from the engine's path-change report: a path is reported many
    /// times while ngtcp2 validates it, alternating between the old address and the one being
    /// probed, and claiming each report churns sockets and drops queued datagrams. By the next
    /// sweep the address has settled, and a repeat of one already claimed costs nothing. Reactor thread only, and quietly does
    /// nothing whenever the claim would be pointless or impossible - a connection that never moved,
    /// a single-reactor server, a client-side connection on its own socket, or the ceiling reached.
    /// </summary>
    internal void QuicPinPeer(QuicConnection conn)
    {
        // Not under KernelFilter: the kernel is already routing by connection id there, so a claim
        // would be a descriptor spent on nothing.
        if (_quicOptions is not { PinMigratedPeers: true, Routing: QuicRouting.Forward } options ||
            !conn.PeerAddressMoved ||
            ShardCount <= 1 ||
            _quicServingFd < 0 ||
            conn.SocketFd != _quicServingFd ||
            conn.PeerAddr == 0 || conn.PeerAddrLen <= 0)
        {
            return;
        }

        // ngtcp2 reports a path more than once while it probes, and each report used to tear the
        // claim down and build a new one - closing a socket with datagrams already queued on it,
        // which the peer then had to retransmit. Losing packets to "optimise" delivery is the wrong
        // way round, so a repeat of the SAME address is left alone.
        ReadOnlySpan<byte> current = new((void*)conn.PeerAddr, conn.PeerAddrLen);
        if (conn.PinSlot >= 0 &&
            conn.PinnedAddrLen == conn.PeerAddrLen &&
            current.SequenceEqual(conn.PinnedAddr.AsSpan(0, conn.PinnedAddrLen)))
        {
            return;
        }

        // A genuinely new address: the old claim names somewhere the peer no longer is, so it goes.
        QuicUnpinPeer(conn);

        if (_quicPinsOpen >= QuicMaxPinnedPeers)
        {
            return;   // keep forwarding: slower, correct, and bounded
        }

        // Never let a failed claim escape. This runs inside ngtcp2's path-change callback, where an
        // exception is caught and recorded as a connection fault - so a transient bind failure would
        // kill a connection that is working perfectly well, to skip an optimisation. Not claiming
        // costs a hop per datagram; faulting costs the connection.
        int fd;
        try
        {
            fd = OpenUdpSocket(options.Port, _config.DualStack, _udp.Gro, _udp.SocketBufferBytes,
                               conn.PeerAddr, conn.PeerAddrLen);
        }
        catch (InvalidOperationException)
        {
            return;   // the port could not be bound again: forwarding continues, which is correct
        }

        if (fd < 0)
        {
            return;   // the address is already claimed, or the kernel refused: forwarding continues
        }

        conn.PinSlot = UdpAdoptSocket(fd, options.Port);

        // Remember WHAT was claimed, so the repeat reports above can be recognised. Without this
        // the comparison never matches and every report rebuilds a working claim.
        current.CopyTo(conn.PinnedAddr);
        conn.PinnedAddrLen = conn.PeerAddrLen;

        _quicPinsOpen++;
        _quicPinsCreated++;
    }

    /// <summary>
    /// Drop this connection's claim, if it holds one. Called on teardown and before re-claiming.
    /// The slot is not reusable yet - it becomes so once the kernel's last completion for it
    /// arrives, which is what stops a stale completion being read as a new socket's traffic.
    /// </summary>
    internal void QuicUnpinPeer(QuicConnection conn)
    {
        int slot = conn.PinSlot;
        if (slot < 0)
        {
            return;
        }

        conn.PinSlot = -1;
        conn.PinnedAddrLen = 0;

        if ((uint)slot >= (uint)_udpFds.Length || _udpFds[slot] < 0)
        {
            return;
        }

        int fd = _udpFds[slot];
        _udpFds[slot] = -1;   // marks it released BEFORE the close, so no re-arm can race the fd away
        close(fd);
        _quicPinsOpen--;
    }

    /// <summary>
    /// Put an already-open socket into the fd table and arm it on this ring, reusing a slot left by
    /// a released pin when one has finished draining. Returns its index.
    /// </summary>
    private int UdpAdoptSocket(int fd, ushort port)
    {
        int index;
        if (_udpFreeSlots.Count > 0)
        {
            index = _udpFreeSlots.Pop();
            _udpFds[index]     = fd;
            _udpFdPorts[index] = port;
        }
        else
        {
            // Append. Indices stay stable - recv completions carry theirs in user_data - so growing
            // the tables cannot disturb the multishots already armed on the existing sockets.
            index       = _udpFds.Length;
            _udpFds     = [.. _udpFds, fd];
            _udpFdPorts = [.. _udpFdPorts, port];
        }

        ArmUdpRecv(index);
        return index;
    }
}
