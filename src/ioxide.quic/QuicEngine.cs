using System.Runtime.InteropServices;
using ioxide;

namespace ioxide.quic;

/// <summary>
/// ngtcp2-backed QUIC engine (server side): loads a certificate/key into a native engine handle and
/// produces the <see cref="QuicConnectionFactory"/> the reactor's QUIC transport calls to adopt new
/// connections. One engine per server; it holds the picotls context every connection shares.
///
/// Usage:
/// <code>
/// var engine = new QuicEngine("cert.pem", "key.pem", cidLength: 8);
/// var config = new ServerConfig
/// {
///     Quic = new QuicOptions { Port = 443, LocalCidLength = 8,
///         ConnectionFactory = engine.CreateFactory((r, in d, in cid) => new MyConnection(engine)) }
/// };
/// </code>
/// </summary>
public sealed unsafe class QuicEngine : IDisposable
{
    private nint _engine;   // iq_engine*

    /// <summary>CID length this endpoint mints; must match <see cref="QuicOptions.LocalCidLength"/>.</summary>
    public uint CidLength { get; }

    /// <summary>
    /// alpn: the protocols this server accepts, preference-ordered (e.g. ["h3"]). A client offering
    /// none of them fails the handshake with no_application_protocol (RFC 9001 §8.1). Null/empty:
    /// accept whichever protocol the client offers first (the pre-H3 permissive behavior).
    /// </summary>
    public QuicEngine(string certPemPath, string keyPemPath, uint cidLength = 8, string[]? alpn = null)
    {
        CidLength = cidLength;

        var cbs = new Ngtcp2.Callbacks
        {
            OnStreamData         = &QuicEngineConnection.CbStreamData,
            OnStreamClose        = &QuicEngineConnection.CbStreamClose,
            OnHandshakeCompleted = &QuicEngineConnection.CbHandshakeCompleted,
            OnNewCid             = &QuicEngineConnection.CbNewCid,
            OnRetireCid          = &QuicEngineConnection.CbRetireCid,
        };

        byte[] alpnWire = AlpnWire(alpn);
        fixed (byte* pAlpn = alpnWire)
        {
            _engine = Ngtcp2.iq_engine_new(certPemPath, keyPemPath, (nuint)cidLength,
                alpnWire.Length > 0 ? pAlpn : null, (nuint)alpnWire.Length, cbs);
        }
        if (_engine == 0)
        {
            throw new InvalidOperationException(
                $"ioxide.quic: engine init failed (cert '{certPemPath}', key '{keyPemPath}')");
        }
    }

    // TLS wire format: each entry length-prefixed (one byte), concatenated.
    private static byte[] AlpnWire(string[]? alpn)
    {
        if (alpn is null || alpn.Length == 0)
        {
            return [];
        }

        var wire = new List<byte>();
        foreach (string proto in alpn)
        {
            byte[] bytes = System.Text.Encoding.ASCII.GetBytes(proto);
            if (bytes.Length is 0 or > 255)
            {
                throw new ArgumentException($"invalid ALPN token '{proto}'", nameof(alpn));
            }
            wire.Add((byte)bytes.Length);
            wire.AddRange(bytes);
        }
        return wire.ToArray();
    }

    /// <summary>
    /// Wrap a user connection constructor into the reactor's factory contract: on a new handshake,
    /// construct the connection, run the ngtcp2 accept + validation, and either adopt it (registering
    /// the server-minted CID) or drop the packet.
    /// </summary>
    /// <summary>Factory for the plain engine connection - the delegate-handler model needs no subclass.</summary>
    public QuicConnectionFactory CreateFactory() => CreateFactory(_ => new QuicEngineConnection(this));

    public QuicConnectionFactory CreateFactory(Func<Reactor, QuicEngineConnection> create)
    {
        return (Reactor reactor, in UdpDatagram datagram, in QuicCid dcid) =>
        {
            QuicEngineConnection conn = create(reactor);

            // The transport fills the base Reactor/SocketFd/PeerAddr *after* the factory returns;
            // TryAccept captures what iq_accept needs now (reactor for callbacks, peer addr from the
            // datagram itself).
            Span<byte> scid = stackalloc byte[(int)CidLength];
            if (!conn.TryAccept(_engine, reactor, in datagram, scid, out int scidLen))
            {
                return null;   // not a valid Initial - drop
            }

            // Route the engine's own SCID to this connection; the transport also records the client
            // DCID that arrived, so both reach us until the handshake retires the bootstrap route.
            reactor.QuicRegisterCid(conn, new QuicCid(scid[..scidLen]));
            return conn;
        };
    }

    /// <summary>Version of the bundled ngtcp2 - also proves the native bundle loads.</summary>
    public static string NativeVersion() => Marshal.PtrToStringUTF8(Ngtcp2.iq_version()) ?? "unknown";

    public void Dispose()
    {
        if (_engine != 0)
        {
            Ngtcp2.iq_engine_free(_engine);
            _engine = 0;
        }
    }
}
