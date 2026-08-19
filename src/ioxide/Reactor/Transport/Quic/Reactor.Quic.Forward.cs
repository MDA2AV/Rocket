using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace ioxide;

/// <summary>
/// Cross-reactor delivery for QUIC: when a datagram lands on a reactor that does not own the
/// connection it names, hand it to the one that does.
///
/// Why it is needed. Every reactor binds the QUIC port with SO_REUSEPORT, and the kernel picks
/// between them by hashing the sender's address. That is stable only while the address is - so a
/// NAT rebind, or a client moving network, re-hashes to a different reactor, one that has never
/// heard of the connection. Its short-header packets are then dropped and the connection dies
/// unreachable on the reactor that could still serve it. Issue #205.
///
/// Why this direction rather than moving the connection. The ngtcp2 conn, the picotls session and
/// the open streams are native state owned by one reactor thread, and QuicConnection is
/// reactor-thread-only throughout. Moving live state to the reactor the packet happened to land on
/// is what shared-nothing forbids; moving the PACKET to the state it belongs to is ordinary message
/// passing, which is how the model is meant to work - and it rides
/// <see cref="ScheduleOnReactor"/>, which reactors already expose for exactly this.
///
/// What crosses a thread boundary is therefore a COPY of the bytes, never a reference to reactor
/// state. That copy is not incidental: the datagram lives in the receiving reactor's io_uring
/// provided-buffer ring, which is returned the moment the dispatch call returns, so handing the
/// owner a pointer into it would be a use-after-free under load.
///
/// Only short-header packets are forwarded. A short header means the handshake is done, so its
/// destination id is one this server minted and its first byte really does name the owner (see
/// iq_stamp_shard in the ngtcp2 shim). A long header carries a connection id the CLIENT chose, and
/// routing on a byte the peer controls would let anyone aim traffic at a reactor of their choosing.
/// </summary>
public sealed unsafe partial class Reactor
{
    /// <summary>
    /// Datagrams that may be awaiting delivery to any one reactor before further ones are dropped.
    /// Dropping is safe here in a way it rarely is: QUIC treats a lost packet as loss and resends,
    /// so the ceiling costs a retransmit rather than a connection. Without it, a reactor that
    /// stalls would let its siblings queue for it without limit.
    /// </summary>
    private const int QuicForwardMaxOutstanding = 1024;

    /// <summary>
    /// The reactors sharing one ServerConfig, so a datagram can be handed to the one that owns it.
    ///
    /// Keyed on the config INSTANCE rather than held in a static, because a process routinely runs
    /// several independent servers at once - every test suite here does - and they must not be able
    /// to see, or post into, each other's reactors. ConditionalWeakTable keys by reference identity
    /// (not the record's value equality) and holds the fleet no longer than the config itself.
    /// </summary>
    private static readonly ConditionalWeakTable<ServerConfig, QuicFleet> QuicFleets = new();

    private sealed class QuicFleet
    {
        public readonly Reactor?[] Members;        // by ShardIndex; null until that reactor starts
        public readonly int[] Outstanding;         // datagrams in flight toward each member
        public readonly ConcurrentQueue<QuicForward> Spare = new();

        public QuicFleet(int count)
        {
            Members     = new Reactor?[count];
            Outstanding = new int[count];
        }
    }

    /// <summary>
    /// One datagram in transit between reactors. Pooled, because a migrated connection forwards
    /// every packet until the peer settles, and an envelope per datagram would be a steady stream
    /// of garbage on the hot path.
    /// </summary>
    private sealed class QuicForward
    {
        public byte[] Payload = [];
        public int    Length;
        public byte[] PeerAddr = new byte[UdpNameCap];
        public int    PeerAddrLen;
        public int    SocketFd;
        public ushort LocalPort;
        public byte   Tos;
        public int    Owner;              // the ShardIndex this was addressed to
        public QuicFleet Fleet = null!;
        public Reactor   Target = null!;
    }

    private QuicFleet? _quicFleet;

    private long _quicForwardsSent;
    private long _quicForwardsReceived;
    private long _quicForwardsDropped;

    /// <summary>Datagrams this reactor handed to a sibling because it did not own the connection.</summary>
    public long QuicForwardsSent => Volatile.Read(ref _quicForwardsSent);

    /// <summary>Datagrams a sibling handed to this reactor.</summary>
    public long QuicForwardsReceived => Volatile.Read(ref _quicForwardsReceived);

    /// <summary>
    /// Datagrams that could not be forwarded and were dropped - the owner's queue was at
    /// <see cref="QuicForwardMaxOutstanding"/>, or it had stopped. Nonzero means a reactor is not
    /// keeping up; the peers will retransmit.
    /// </summary>
    public long QuicForwardsDropped => Volatile.Read(ref _quicForwardsDropped);

    // Join the fleet. Called from InitQuic, on the reactor's own thread, before the loop starts.
    private void QuicJoinFleet()
    {
        if (ShardCount <= 1 || (uint)_id >= (uint)ShardCount)
        {
            return;   // nothing to forward to, or an id outside the configured fleet
        }

        QuicFleet fleet = QuicFleets.GetValue(_config, static c => new QuicFleet(c.ReactorCount));
        _quicFleet = fleet;
        Volatile.Write(ref fleet.Members[_id], this);
    }

    /// <summary>
    /// Hand a datagram to the reactor that owns its connection. Returns false when this reactor is
    /// the owner (so the id is simply unknown - stale, retired, or hostile) or when there is
    /// nowhere to send it, in which case the caller drops as before.
    /// </summary>
    private bool QuicTryForward(in UdpDatagram datagram, in QuicCid dcid)
    {
        QuicFleet? fleet = _quicFleet;
        if (fleet is null || dcid.Length == 0)
        {
            return false;
        }

        int owner = dcid.FirstByte % fleet.Members.Length;
        if (owner == _id)
        {
            return false;   // addressed here, and we do not have it: not a routing problem
        }

        Reactor? target = Volatile.Read(ref fleet.Members[owner]);
        if (target is null || target._stopRequested || target._wakeFd <= 0)
        {
            _quicForwardsDropped++;
            return true;    // handled: there is nothing better to do with it than drop it
        }

        // Reserve a slot before copying, so a stalled owner cannot make its siblings do the work.
        if (Interlocked.Increment(ref fleet.Outstanding[owner]) > QuicForwardMaxOutstanding)
        {
            Interlocked.Decrement(ref fleet.Outstanding[owner]);
            _quicForwardsDropped++;
            return true;
        }

        if (!fleet.Spare.TryDequeue(out QuicForward? forward))
        {
            forward = new QuicForward();
        }

        int length = datagram.Payload.Length;
        if (forward.Payload.Length < length)
        {
            if (forward.Payload.Length > 0)
            {
                ArrayPool<byte>.Shared.Return(forward.Payload);
            }
            forward.Payload = ArrayPool<byte>.Shared.Rent(length);
        }

        // The copy that makes this safe. Payload points into the recv slot, which goes back to the
        // provided-buffer ring as soon as this dispatch returns.
        datagram.Payload.CopyTo(forward.Payload);
        forward.Length = length;

        int addrLen = Math.Min(datagram.PeerAddrLen, UdpNameCap);
        new ReadOnlySpan<byte>((void*)datagram.PeerAddr, addrLen).CopyTo(forward.PeerAddr);
        forward.PeerAddrLen = addrLen;

        forward.SocketFd  = datagram.SocketFd;
        forward.LocalPort = datagram.LocalPort;
        forward.Tos       = datagram.Tos;
        forward.Owner     = owner;
        forward.Fleet     = fleet;
        forward.Target    = target;

        _quicForwardsSent++;

        // Static lambda over the envelope alone: no closure, no per-datagram allocation.
        target.ScheduleOnReactor(
            static state =>
            {
                var envelope = (QuicForward)state!;
                envelope.Target.QuicReceiveForward(envelope);
            },
            forward);

        return true;
    }

    // Runs on the OWNING reactor's thread, out of its post queue.
    private void QuicReceiveForward(QuicForward forward)
    {
        QuicFleet fleet = forward.Fleet;

        try
        {
            _quicForwardsReceived++;

            fixed (byte* payload = forward.Payload)
            fixed (byte* addr = forward.PeerAddr)
            {
                // GRO segment size 0: trains are split before routing, so a forwarded datagram is
                // always a single one.
                QuicDispatchDatagram(new UdpDatagram(forward.SocketFd, forward.LocalPort,
                    (nint)addr, forward.PeerAddrLen,
                    new ReadOnlySpan<byte>(payload, forward.Length), 0, forward.Tos));
            }
        }
        finally
        {
            Interlocked.Decrement(ref fleet.Outstanding[forward.Owner]);
            forward.Fleet  = null!;
            forward.Target = null!;
            fleet.Spare.Enqueue(forward);
        }
    }
}
