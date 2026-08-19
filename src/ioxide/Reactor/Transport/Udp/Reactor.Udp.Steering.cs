using System.Runtime.CompilerServices;
using static ioxide.Native;

namespace ioxide;

/// <summary>
/// Connection-id steering for QUIC: a classic-BPF program attached to the QUIC port's
/// <c>SO_REUSEPORT</c> group so the kernel picks the reactor by reading the connection id out of
/// the datagram, instead of hashing the sender's address.
///
/// The default without it is the 4-tuple hash, which is correct only while a client's address
/// never changes. When it does - a NAT rebind, a phone moving from wifi to cellular, a deliberate
/// migration - the hash lands the datagram on a reactor that has never heard of that connection,
/// and a short-header packet for an unknown id is dropped. The connection stays alive and
/// unreachable on its own reactor until the idle sweep evicts it. Since the state that could serve
/// it (the ngtcp2 conn, the picotls session, the open streams) is native memory owned by one
/// reactor thread and documented reactor-thread-only, the fix has to move the DATAGRAM to the
/// state, never the state to the datagram.
///
/// Which is what the connection id is for. Every id this server mints carries its owning reactor
/// in the first byte (see iq_stamp_shard in the shim), chosen so <c>cid[0] % ReactorCount</c> is
/// exactly that reactor. The filter below recomputes it and returns it as the index into the
/// reuseport group. The id travels with the connection, so the routing survives whatever the
/// address does.
///
/// Two things this depends on, both handled here:
///
/// <list type="bullet">
/// <item>The filter answers with a position in the reuseport group, and that position is
/// <b>bind order</b>. So the reactors have to open the QUIC socket in <c>ShardIndex</c> order,
/// which is what <see cref="QuicSteeringAwaitTurn"/> arranges. It is a startup-only cost.</item>
/// <item>The program must not be attached until every reactor has joined the group, or an index
/// can point past the end of it. The last reactor out attaches it.</item>
/// </list>
///
/// If anything about that does not hold - the kernel refuses the program, a reactor fails to bind,
/// the fleet never assembles - steering is abandoned and the port keeps the 4-tuple hash. That is
/// today's behaviour, so the fallback is never worse than not having tried.
/// </summary>
public sealed unsafe partial class Reactor
{
    /// <summary>Attach a reuseport-steering program (Linux 4.5+). Not in <c>SocketOptionName</c>.</summary>
    private const int SO_ATTACH_REUSEPORT_CBPF = 51;

    /// <summary>
    /// How long a reactor waits for its turn to open the QUIC socket before giving up on ordering.
    /// Generous, because it only has to cover other reactor threads reaching the same point in
    /// startup; if it is ever hit, something is wrong with the fleet rather than merely slow.
    /// </summary>
    private const int QuicSteeringTurnTimeoutMs = 10_000;

    /// <summary>
    /// The shard byte is one byte, so beyond 256 reactors it cannot encode the owner and steering
    /// is not attempted. Nothing else changes; the port keeps the 4-tuple hash.
    /// </summary>
    private const int QuicSteeringMaxShards = 256;

    // Keyed by the ServerConfig instance the fleet shares, so two servers in one process (which is
    // the normal shape in the test suites) get independent gates and cannot wait on each other.
    // ConditionalWeakTable keys on reference identity, not the record's value equality, which is
    // what we want here - and it holds the gate no longer than the config itself.
    private static readonly ConditionalWeakTable<ServerConfig, QuicSteeringGate> QuicSteeringGates = new();

    private sealed class QuicSteeringGate
    {
        public readonly object Lock = new();
        public int  Turn;            // the ShardIndex allowed to open right now
        public int  QuicFd = -1;     // any socket in the group; attaching to one sets it for all
        public bool Abandoned;       // ordering broke, so the indices cannot be trusted
        public bool Settled;         // attach (or the decision not to) already happened
    }

    /// <summary>
    /// Whether this reactor should take part in ordered opening. QUIC only - a plain UDP server
    /// keeps today's startup exactly - and only when there is a fleet to steer across.
    /// </summary>
    private bool _quicSteeringAttached;

    /// <summary>
    /// True on the reactor that attached the steering program (the last one to bind, since the
    /// group has to be complete first). The filter is group-wide, so one reactor reporting this is
    /// the fleet reporting it. False everywhere under <see cref="QuicRouting.Forward"/>, and false
    /// when the kernel refused the program.
    /// </summary>
    public bool QuicKernelSteeringAttached => _quicSteeringAttached;

    private bool QuicSteeringActive =>
        _config.Quic is { Routing: QuicRouting.KernelFilter } &&
        ShardCount > 1 &&
        ShardCount <= QuicSteeringMaxShards;

    private QuicSteeringGate? QuicSteeringBegin()
    {
        if (!QuicSteeringActive)
        {
            return null;
        }

        QuicSteeringGate gate = QuicSteeringGates.GetValue(_config, static _ => new QuicSteeringGate());
        QuicSteeringAwaitTurn(gate);
        return gate;
    }

    /// <summary>
    /// Block until it is this reactor's turn to open its sockets, so that group position equals
    /// <see cref="ShardIndex"/>.
    ///
    /// The timeout is what keeps a misconfigured fleet from becoming a hang: a caller that starts
    /// fewer reactors than <see cref="ServerConfig.ReactorCount"/> would otherwise leave everyone
    /// after the missing one waiting forever. On expiry the gate is abandoned ONCE and every
    /// waiter is released together, so the delay is paid a single time rather than per reactor.
    /// </summary>
    private void QuicSteeringAwaitTurn(QuicSteeringGate gate)
    {
        lock (gate.Lock)
        {
            long deadline = Environment.TickCount64 + QuicSteeringTurnTimeoutMs;

            while (gate.Turn != ShardIndex && !gate.Abandoned)
            {
                int remaining = (int)(deadline - Environment.TickCount64);
                if (remaining <= 0 || !Monitor.Wait(gate.Lock, remaining))
                {
                    Console.Error.WriteLine(
                        $"[r{_id}] quic: waited {QuicSteeringTurnTimeoutMs} ms for reactor {gate.Turn} "
                        + "to bind; giving up on connection-id steering and falling back to "
                        + "cross-reactor forwarding");
                    gate.Abandoned = true;
                    Monitor.PulseAll(gate.Lock);
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Hand the turn to the next reactor, and - if this was the last one and every reactor bound
    /// cleanly - attach the steering program now that the group is complete.
    /// </summary>
    /// <param name="gate">The fleet's gate, or null when steering is not active.</param>
    /// <param name="quicFd">This reactor's QUIC socket, or -1 if opening it failed.</param>
    private void QuicSteeringRelease(QuicSteeringGate? gate, int quicFd)
    {
        if (gate is null)
        {
            return;
        }

        lock (gate.Lock)
        {
            if (quicFd < 0)
            {
                // This reactor never joined the group, so every later index is off by one.
                gate.Abandoned = true;
            }
            else if (gate.QuicFd < 0)
            {
                gate.QuicFd = quicFd;
            }

            gate.Turn++;
            Monitor.PulseAll(gate.Lock);

            if (gate.Turn < ShardCount || gate.Settled)
            {
                return;
            }

            gate.Settled = true;
            if (gate.Abandoned || gate.QuicFd < 0)
            {
                return;   // already reported; the port keeps the 4-tuple hash
            }

            QuicAttachSteering(gate.QuicFd, ShardCount);
        }
    }

    /// <summary>
    /// Attach the steering program to the QUIC reuseport group. Failure is reported and otherwise
    /// ignored: an older kernel, a seccomp policy or a restricted container can all refuse it, and
    /// the only consequence is that address changes go back to breaking connections.
    /// </summary>
    private void QuicAttachSteering(int fd, int shards)
    {
        // classic-BPF instruction: { u16 code; u8 jt; u8 jf; u32 k; }
        //
        //   0  ld len                 A = datagram length
        //   1  jge #9 ? next : ->8    too short to hold a connection id: fall through on the
        //                             length itself, which is in bounds and deterministic
        //   2  ldb [0]                the QUIC first byte
        //   3  and #0x80              its header-form bit
        //   4  jeq #0 ? ->7 : next    clear = short header
        //   5  ldb [6]                long header: first byte / 4 version / 1 dcid len, so DCID
        //                             starts at 6. A client's Initial id is its own random value,
        //                             which makes this a hash - and a stable one, so every packet
        //                             of a handshake still reaches one reactor.
        //   6  ja ->8
        //   7  ldb [1]                short header: DCID starts straight after the first byte.
        //                             This is a connection id WE minted, so the byte is the shard.
        //   8  mod #shards            the reuseport index
        //   9  ret a
        //
        // Byte loads, not word loads, because iq_stamp_shard controls exactly one byte. Reading
        // more would mix in bytes it does not constrain and the two sides would disagree.
        (ushort code, byte jt, byte jf, uint k)[] program =
        [
            (0x80, 0, 0, 0),               // ld len
            (0x35, 0, 6, 9),               // jge #9
            (0x30, 0, 0, 0),               // ldb [0]
            (0x54, 0, 0, 0x80),            // and #0x80
            (0x15, 2, 0, 0),               // jeq #0
            (0x30, 0, 0, 6),               // ldb [6]
            (0x05, 0, 0, 1),               // ja
            (0x30, 0, 0, 1),               // ldb [1]
            (0x94, 0, 0, (uint)shards),    // mod #shards
            (0x16, 0, 0, 0),               // ret a
        ];

        byte* instructions = stackalloc byte[program.Length * 8];
        for (int i = 0; i < program.Length; i++)
        {
            byte* at = instructions + i * 8;
            *(ushort*)at = program[i].code;
            at[2]        = program[i].jt;
            at[3]        = program[i].jf;
            *(uint*)(at + 4) = program[i].k;
        }

        // struct sock_fprog { unsigned short len; struct sock_filter *filter; } - the pointer is
        // 8-aligned, so the length sits in the first two bytes of a 16-byte struct.
        byte* fprog = stackalloc byte[16];
        new Span<byte>(fprog, 16).Clear();
        *(ushort*)fprog     = (ushort)program.Length;
        *(byte**)(fprog + 8) = instructions;

        if (setsockopt(fd, SOL_SOCKET, SO_ATTACH_REUSEPORT_CBPF, fprog, 16) < 0)
        {
            Console.Error.WriteLine(
                $"[r{_id}] quic: could not attach connection-id steering; the port keeps the "
                + "4-tuple hash and migrated clients fall back to cross-reactor forwarding, which "
                + "is correct but costs a hop per datagram");
            return;
        }

        _quicSteeringAttached = true;
        Console.WriteLine($"[r{_id}] quic: connection-id steering attached across {shards} reactors");
    }
}
