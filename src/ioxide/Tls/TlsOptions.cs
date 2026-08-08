namespace ioxide.tls;

public sealed class TlsOptions
{
    /// <summary>PEM certificate chain file. Exactly one of this and <see cref="CertificatePem"/>
    /// must be set - <see cref="TlsService.Start"/> refuses anything else.</summary>
    public string? CertificatePath { get; init; }

    /// <summary>PEM private key file. Exactly one of this and <see cref="KeyPem"/> must be set.</summary>
    public string? KeyPath { get; init; }

    /// <summary>
    /// The certificate chain as PEM text, leaf first - the in-memory alternative to
    /// <see cref="CertificatePath"/>, for hosts that carry certificates as data (an
    /// <c>X509Certificate2</c> export, a secrets store) rather than as files on disk.
    /// </summary>
    public string? CertificatePem { get; init; }

    /// <summary>The private key as PEM text - the in-memory alternative to <see cref="KeyPath"/>.</summary>
    public string? KeyPem { get; init; }

    /// <summary>
    /// Protocols this port serves, MOST PREFERRED FIRST. The client sends what it supports and the
    /// server picks; listing <c>["h2", "http/1.1"]</c> means a browser offering both gets HTTP/2.
    ///
    /// Order is the only preference ALPN has - RFC 7301 carries a plain ordered list with no
    /// quality values, so there is nothing weight-like to express. Server preference wins here
    /// (this list is walked, and the first entry the client also offered is chosen), which is what
    /// nginx and Kestrel do: the server knows which protocol it serves better.
    ///
    /// A client offering nothing we list gets no ALPN extension back and continues without one,
    /// rather than being rejected - it may still speak HTTP/1.1 perfectly well.
    /// </summary>
    public string[] Alpn { get; init; } = ["http/1.1"];

    /// <summary>
    /// Let the kernel decrypt inbound records too. Off by default, experimental, and it requires
    /// <see cref="KernelTx"/>: RX is programmed at the same handoff as TX and shares the TCP_ULP
    /// that EnableTx installs, so asking for RX alone is refused at
    /// <see cref="TlsService.Start"/>.
    ///
    /// With both on, an ordinary recv returns PLAINTEXT and <see cref="TlsSession.Decrypt"/> is a
    /// no-op - plaintext then lands directly in ring memory, so the zero-copy reader works on TLS
    /// connections exactly as it does on cleartext ones.
    ///
    /// Two reasons it is opt-in even where <see cref="KernelTx"/> is.
    ///
    /// The handoff must land on a record boundary. Whatever the handshake already pulled off the
    /// socket is invisible to the kernel, so the record sequence it starts at has to account for
    /// it - and if the handshake left a PARTIAL record behind, those bytes are gone and no sequence
    /// number can recover them. That connection silently stays on the userspace path.
    ///
    /// And a recv can then fail with EIO. With kTLS RX a non-application-data record - a TLS 1.3
    /// KeyUpdate, or an alert - is only retrievable through recvmsg with a TLS_GET_RECORD_TYPE
    /// control message. The reactor's TCP hot path uses IORING_OP_RECV, which carries no control
    /// data, so the kernel refuses the read instead. Clients that never send one are unaffected;
    /// one that does loses the connection.
    /// </summary>
    public bool KernelRx { get; init; }

    /// <summary>
    /// Let the kernel encrypt outbound records. <b>Off by default</b> - TLS is OpenSSL in both
    /// directions unless you turn this on.
    ///
    /// It used to default on, which made ioxide's TLS asymmetric: the kernel encrypted, OpenSSL
    /// decrypted. That asymmetry is now something you opt into rather than something you inherit,
    /// because it is not free and it was not faster. Measured on loopback, HTTP/1.1, 4 reactors:
    /// kTLS trails OpenSSL by roughly 20-25% on large single writes and costs about 20% more CPU
    /// per request, while a small response shows no difference either way. What it constrains is
    /// the part that matters: the Linux <c>tls</c> module has to be present, TLS 1.3 only, one
    /// ciphersuite, and session resumption is disabled because a ticket would consume a record
    /// sequence number and desynchronise the handoff.
    ///
    /// Turn it on for what it is actually for - <c>sendfile</c> and NICs that offload TLS - neither
    /// of which those measurements exercise.
    ///
    /// <b>It changes what a handler must write.</b> With kTLS the kernel produces the records, so
    /// a handler writes plaintext straight to the connection; without it OpenSSL has to encrypt
    /// first. <see cref="TlsSession.Write"/> is correct either way and is what samples use - a bare
    /// <c>connection.Write</c> on a session with this off puts CLEARTEXT on the wire.
    /// </summary>
    public bool KernelTx { get; init; }
}
