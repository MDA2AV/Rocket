using System.Runtime.InteropServices;
using ioxide;

namespace ioxide.ngtcp2;

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

    // Set once a factory exists, which is the point past which reactors may read the SNI table.
    private bool _serving;

    // Serialises rotations against each other and against disposal. Handshakes never take it: they
    // read one pointer. Without it two rotations can each publish a generation linking the SAME
    // predecessor, which loses one of them - it is left out of the chain the engine frees, so its
    // certificates and keys are never released and one of the two renewals silently did nothing.
    private readonly Lock _rotation = new();

    /// <summary>CID length this endpoint mints; must match <see cref="QuicOptions.LocalCidLength"/>.</summary>
    public uint CidLength { get; }

    /// <summary>
    /// Per-connection send-retention high-water: how many unacknowledged response bytes a
    /// connection buffers before it applies backpressure (the egress pump stops pulling more of a
    /// response until acks drain it). Bounds memory to roughly this per connection regardless of
    /// response size, so a large file streams through without buffering all of it. Default 16 MiB.
    /// </summary>
    public long MaxSendRetentionBytes { get; }

    /// <summary>
    /// alpn: the protocols this server accepts, preference-ordered (e.g. ["h3"]). A client offering
    /// none of them fails the handshake with no_application_protocol (RFC 9001 §8.1). Null/empty:
    /// accept whichever protocol the client offers first (the pre-H3 permissive behavior).
    /// </summary>
    /// <param name="clientCaPemPath">
    /// PEM bundle that client certificates are validated against, as a path - mutual TLS. Null (the
    /// default), with <paramref name="clientCaPem"/> also null, leaves it off and the handshake is
    /// exactly what it was.
    ///
    /// QUIC settles client authentication during the handshake and RFC 9001 section 4.4 forbids
    /// doing it afterwards, so this is a property of the whole connection: there is no asking for a
    /// certificate later because a request happened to reach a protected route.
    /// </param>
    /// <param name="clientCaPem">
    /// The same trust anchors as PEM text - the in-memory alternative to
    /// <paramref name="clientCaPemPath"/>, for a host that carries its CA bundle as data rather than
    /// as a file. Set at most one of the two.
    /// </param>
    /// <param name="requireClientCertificate">
    /// With a CA configured, whether a client offering no certificate is refused during the
    /// handshake. False lets it connect unauthenticated and leaves the decision to the application,
    /// which can read <see cref="QuicEngineConnection.PeerSubject"/>.
    /// </param>
    public QuicEngine(string certPemPath, string keyPemPath, uint cidLength = 8, string[]? alpn = null,
        long maxSendRetentionBytes = 16L << 20,
        string? clientCaPemPath = null, bool requireClientCertificate = false,
        string? clientCaPem = null)
    {
        if (clientCaPemPath is not null && clientCaPem is not null)
        {
            throw new ArgumentException(
                "At most one client CA source: set clientCaPemPath or clientCaPem, not both.", nameof(clientCaPem));
        }

        // Requiring a certificate with nothing to validate it against would ask every client for
        // one, refuse the ones that have none, and then accept whatever the rest sent - a port that
        // reads as authenticated and is not. The TCP side refuses the same combination.
        if (requireClientCertificate && clientCaPemPath is null && clientCaPem is null)
        {
            throw new ArgumentException(
                "requireClientCertificate needs trust anchors: set clientCaPemPath or clientCaPem, " +
                "or drop requireClientCertificate.", nameof(requireClientCertificate));
        }

        // QUIC caps a connection ID at 20 bytes and ngtcp2's ngtcp2_cid is sized to exactly that,
        // so a longer one would be written past the end of a stack struct while minting the
        // server's CID. Zero-length is legal QUIC but not here: the reactor demultiplexes inbound
        // datagrams by CID prefix and has nothing to route on without one.
        if (cidLength is < 1 or > 20)
        {
            throw new ArgumentOutOfRangeException(nameof(cidLength), cidLength,
                "QUIC connection ID length must be 1..20 bytes.");
        }

        CidLength = cidLength;
        // Clamp to a floor: the pump overshoots the high-water by at most one egress chunk (16 KiB),
        // so a cap below that would wedge a response mid-flight. 256 KiB gives comfortable headroom.
        MaxSendRetentionBytes = Math.Max(maxSendRetentionBytes, 256L << 10);

        var callbacks = new Ngtcp2.Callbacks
        {
            OnStreamData         = &QuicEngineConnection.CbStreamData,
            OnStreamClose        = &QuicEngineConnection.CbStreamClose,
            OnHandshakeCompleted = &QuicEngineConnection.CbHandshakeCompleted,
            OnNewCid             = &QuicEngineConnection.CbNewCid,
            OnRetireCid          = &QuicEngineConnection.CbRetireCid,
            OnStreamReset        = &QuicEngineConnection.CbStreamReset,
            OnStreamStopSending  = &QuicEngineConnection.CbStreamStopSending,
            OnAckedStreamData    = &QuicEngineConnection.CbAckedStreamData,
        };

        byte[] alpnWire = AlpnWire(alpn);
        fixed (byte* pAlpn = alpnWire)
        {
            _engine = Ngtcp2.iq_engine_new_mtls(certPemPath, keyPemPath, (nuint)cidLength,
                alpnWire.Length > 0 ? pAlpn : null, (nuint)alpnWire.Length,
                clientCaPemPath, clientCaPem, requireClientCertificate ? 1 : 0, callbacks);
        }
        if (_engine == 0)
        {
            throw new InvalidOperationException(
                $"ioxide.ngtcp2: engine init failed (cert '{certPemPath}', key '{keyPemPath}'"
                + (clientCaPemPath is not null ? $", client CA '{clientCaPemPath}')"
                    : clientCaPem is not null ? ", client CA from PEM text)" : ")"));
        }
    }

    /// <summary>
    /// Serves <paramref name="certificatePath"/> to clients asking for <paramref name="host"/> by
    /// name, instead of the certificate this engine was built with - Server Name Indication
    /// (RFC 6066 3.1), which is what lets one UDP port answer for several hosts.
    /// </summary>
    /// <remarks>
    /// Call before <see cref="CreateFactory()"/>, and this REFUSES afterwards. The table is read
    /// during the handshake on whichever reactor the connection landed on, with no lock; adding to
    /// it while those reads are running is a write beside them, and the reader would be walking
    /// memory the write is in the middle of moving. Enforced rather than documented because the
    /// consequence is not a wrong answer - it is a native use-after-free.
    ///
    /// The certificate given to the constructor stays the default: a client that sends no name, or
    /// asks for one not registered here, is answered with it rather than refused. Client
    /// verification carries over unchanged, so a named host cannot quietly serve without the mutual
    /// TLS the engine was configured for.
    ///
    /// A name already registered is refused rather than shadowed, since the first registration is
    /// the one that would answer.
    ///
    /// PEM paths, for the same reason the constructor takes them: ngtcp2 loads certificates by path.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The engine is already serving, the name is already registered, or the certificate or key
    /// could not be loaded.
    /// </exception>
    /// <exception cref="ArgumentException">The name is blank.</exception>
    public void AddHost(string host, string certificatePath, string keyPath)
    {
        // Before the name is judged: on a serving engine that is the reason the call is refused,
        // whatever the name looks like, and reporting the name first would send the caller after
        // the wrong problem.
        if (_serving)
        {
            throw new InvalidOperationException(
                $"ioxide.ngtcp2: cannot add '{host}' once the engine is serving - register every host " +
                $"before CreateFactory. The handshake reads the table without a lock; to change what " +
                $"a running engine serves, use {nameof(ReplaceCertificates)}.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        if (Ngtcp2.iq_engine_add_host(_engine, host, certificatePath, keyPath) != 0)
        {
            throw new InvalidOperationException(
                $"ioxide.ngtcp2: could not serve '{host}' from '{certificatePath}' / '{keyPath}'.");
        }
    }

    /// <summary>
    /// Replaces every certificate this engine serves, on a running server - the default and each
    /// host. Connections already established keep the certificate they were given; the next
    /// handshake gets the new one.
    /// </summary>
    /// <param name="defaultCertificate">
    /// The certificate for a client that sends no name, or asks for one not in
    /// <paramref name="certificatesByHost"/>.
    /// </param>
    /// <param name="certificatesByHost">The certificates chosen by name, or null to serve only the default.</param>
    /// <remarks>
    /// What renewal needs. An ACME client rewrites its PEM every couple of months, and without this
    /// the only way to serve the new one is to restart - so pass the same paths and their new
    /// contents are read.
    ///
    /// It replaces rather than edits, and that is the point: a certificate outlives the call that
    /// installs it, since picotls keeps reading the context for as long as a connection lives.
    /// A whole new set is built first and published with one store, so a reader sees either every
    /// old certificate or every new one, never a mixture.
    ///
    /// Nothing is published unless all of it built. A bad path or an unreadable key throws and
    /// LEAVES THE ENGINE SERVING WHAT IT WAS - a failed renewal is a server that kept working.
    ///
    /// What it does NOT change: the client trust anchors and whether a client certificate is
    /// required. Those belong to the engine, so renewing a certificate cannot quietly change who is
    /// allowed to connect.
    ///
    /// The certificates it replaces are kept, not freed: a handshake may be between reading them
    /// and using what it found, and picotls does not refcount contexts. They are released when the
    /// engine is disposed. Renewing a handful of names a few times a year costs kilobytes.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The certificates could not be loaded; the engine still serves the previous ones.</exception>
    public void ReplaceCertificates(QuicCertificate defaultCertificate,
        IReadOnlyDictionary<string, QuicCertificate>? certificatesByHost = null)
    {
        ArgumentNullException.ThrowIfNull(defaultCertificate);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultCertificate.CertificatePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultCertificate.KeyPath);

        // Validated before anything is allocated, so the cleanup below only ever has to undo
        // allocation rather than half a validation pass.
        foreach ((string host, QuicCertificate certificate) in
                 certificatesByHost ?? (IReadOnlyDictionary<string, QuicCertificate>)new Dictionary<string, QuicCertificate>())
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(host);
            ArgumentNullException.ThrowIfNull(certificate);
            ArgumentException.ThrowIfNullOrWhiteSpace(certificate.CertificatePath);
            ArgumentException.ThrowIfNullOrWhiteSpace(certificate.KeyPath);
        }

        lock (_rotation)
        {
            ObjectDisposedException.ThrowIf(_engine == 0, this);
            Replace(defaultCertificate, certificatesByHost);
        }
    }

    private void Replace(QuicCertificate defaultCertificate,
        IReadOnlyDictionary<string, QuicCertificate>? certificatesByHost)
    {
        int count = certificatesByHost?.Count ?? 0;

        // Marshalled by hand: the runtime cannot pair an array with UTF-8 elements, and the shim
        // takes UTF-8 everywhere else. Freed below whatever happens - the shim copies what it keeps.
        nint[] hosts = new nint[count], certs = new nint[count], keys = new nint[count];
        int filled = 0;

        try
        {
            if (certificatesByHost is not null)
            {
                foreach ((string host, QuicCertificate certificate) in certificatesByHost)
                {
                    hosts[filled] = Marshal.StringToCoTaskMemUTF8(host);
                    certs[filled] = Marshal.StringToCoTaskMemUTF8(certificate.CertificatePath);
                    keys[filled] = Marshal.StringToCoTaskMemUTF8(certificate.KeyPath);
                    filled++;
                }
            }

            int result;

            fixed (nint* h = hosts, c = certs, k = keys)
            {
                result = Ngtcp2.iq_engine_replace_certificates(_engine,
                    defaultCertificate.CertificatePath, defaultCertificate.KeyPath, h, c, k, (nuint)filled);
            }

            if (result != 0)
            {
                throw new InvalidOperationException(
                    $"ioxide.ngtcp2: could not replace the certificates from " +
                    $"'{defaultCertificate.CertificatePath}' / '{defaultCertificate.KeyPath}'. " +
                    "The engine still serves the ones it had.");
            }
        }
        finally
        {
            for (int i = 0; i < filled; i++)
            {
                Marshal.FreeCoTaskMem(hosts[i]);
                Marshal.FreeCoTaskMem(certs[i]);
                Marshal.FreeCoTaskMem(keys[i]);
            }
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
        // From here the SNI table can be read by any reactor a handshake lands on, so it is closed
        // to further writes. See AddHost.
        _serving = true;

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
        // Under the same lock a rotation takes: disposing frees every generation, and a rotation
        // in flight is reading and building from them. Connections are a separate matter - the
        // engine still has to outlive those, as it always did.
        lock (_rotation)
        {
            if (_engine != 0)
            {
                Ngtcp2.iq_engine_free(_engine);
                _engine = 0;
            }
        }
    }
}
