namespace ioxide.tls;

public sealed class TlsOptions
{
    /// <summary>PEM certificate chain file.</summary>
    public required string CertificatePath { get; init; }

    /// <summary>PEM private key file.</summary>
    public required string KeyPath { get; init; }

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
    /// Let the kernel decrypt inbound records too, not just encrypt outbound ones. Off by default.
    ///
    /// TLS in ioxide is asymmetric: kTLS TX is programmed for every connection, so responses are
    /// encrypted by the kernel on the existing send path, while inbound records are decrypted in
    /// userspace by OpenSSL. Turning this on programs TLS_RX as well, after which an ordinary recv
    /// returns PLAINTEXT and <see cref="TlsSession.Decrypt"/> is a no-op - plaintext then lands
    /// directly in ring memory, so the zero-copy reader works on TLS connections exactly as it does
    /// on cleartext ones.
    ///
    /// Two reasons it is opt-in.
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
    /// Let the kernel encrypt outbound records. <b>On by default</b> - this is ioxide's normal TLS
    /// write path, and the reason handlers write plaintext.
    ///
    /// Turning it off keeps everything in OpenSSL: the socket gets no TLS ULP at all, handlers must
    /// hand responses to <see cref="TlsSession.WriteEncrypted"/> instead of writing them straight
    /// to the connection, and <c>MSG_WAITALL</c> stays on because there is no kTLS to reject it.
    ///
    /// The trade is one copy. kTLS takes plaintext into the write slab and encrypts on send; OpenSSL
    /// encrypts into its write BIO and the records are then read into the slab. What that costs in
    /// practice is a question worth measuring rather than assuming - and turning kTLS off also
    /// drops its constraints: no 'tls' kernel module, no TLS-1.3-only, no single-ciphersuite limit,
    /// and no handshake-alignment problem.
    /// </summary>
    public bool KernelTx { get; init; } = true;
}
