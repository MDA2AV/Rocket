using System.IO.Pipelines;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace ioxide.tls;

/// <summary>
/// The per-connection TLS state once the handshake is done: OpenSSL decrypts inbound records and
/// encrypts outbound ones, except for whichever direction was handed to the kernel (opt-in, see
/// <see cref="TlsOptions.KernelTx"/>). One session per connection, reactor-thread only.
/// </summary>
public sealed unsafe class TlsSession : IDisposable
{
    // Defensive cap on plaintext produced by a single Decrypt. In normal operation this is bounded
    // by the ciphertext slice fed per call (one recv buffer) - decryption never expands - so the cap
    // is a guard against a pathological stream, not the everyday bound.
    private const int MaxPlaintextBytes = 8 * 1024 * 1024;

    private readonly nint _ssl;
    private readonly nint _rbio;
    private readonly nint _wbio;
    private byte[] _plain = new byte[16 * 1024];
    private int _plainLen;
    private bool _disposed;

    private GCHandle _handle;   // roots this session so the keylog callback can reach it via SSL ex_data
    private int _fd = -1;       // the connection's fd - the teardown close_notify goes out on it
    private bool _txEnabled;

    /// <summary>The captured TLS 1.3 server traffic secret (set by the keylog callback during the handshake).</summary>
    internal byte[]? ServerSecret;

    /// <summary>The client traffic secret - the RX half, needed only when kTLS decrypts inbound.</summary>
    internal byte[]? ClientSecret;

    /// <summary>
    /// True once the kernel decrypts inbound records for this connection. Reads then arrive as
    /// plaintext and <see cref="Decrypt"/> has nothing left to do.
    /// </summary>
    public bool KernelRx { get; private set; }

    /// <summary>True when the kernel encrypts outbound records - the opt-in, not the default.
    /// Handlers do not branch on this: <see cref="Write"/> is correct either way, and this only
    /// reports which path it takes.</summary>
    public bool KernelTx => _txEnabled;

    internal void MarkKernelRx() => KernelRx = true;

    /// <summary>True once the peer sent close_notify.</summary>
    public bool Closed { get; private set; }

    /// <summary>
    /// The protocol ALPN settled on, or null when the client offered none this port serves. This
    /// is how a handler serving several protocols on one port knows which loop to run - without
    /// it, advertising both h2 and http/1.1 tells the client something the server cannot act on.
    /// </summary>
    public string? NegotiatedAlpn { get; private set; }

    internal void CaptureAlpn()
    {
        byte* data;
        uint length;
        OpenSsl.SSL_get0_alpn_selected(_ssl, &data, &length);
        NegotiatedAlpn = data == null || length == 0
            ? null
            : System.Text.Encoding.ASCII.GetString(data, (int)length);
    }

    /// <summary>
    /// Subject of the client certificate this peer presented, or null when it presented none -
    /// which is possible whenever <see cref="TlsOptions.ClientCaPath"/> is set but
    /// <see cref="TlsOptions.RequireClientCertificate"/> is not.
    ///
    /// A value here means the chain VALIDATED: an invalid one fails the handshake, so a connection
    /// that reached a handler never carries a certificate that was merely offered. Null means
    /// unauthenticated, and is the whole decision a handler serving a mixed port has to make.
    /// </summary>
    public string? PeerSubject { get; private set; }

    internal void CapturePeerCertificate()
    {
        // Borrowed, not owned - get0 takes no reference, so there is nothing to free.
        nint cert = OpenSsl.SSL_get0_peer_certificate(_ssl);
        if (cert == 0)
        {
            PeerSubject = null;
            return;
        }

        // Belt and braces: with SSL_VERIFY_PEER and no callback OpenSSL has already failed the
        // handshake on a bad chain, so this cannot report a name that was not verified.
        if (OpenSsl.SSL_get_verify_result(_ssl) != OpenSsl.X509_V_OK)
        {
            PeerSubject = null;
            return;
        }

        nint name = OpenSsl.X509_get_subject_name(cert);
        if (name == 0)
        {
            PeerSubject = null;
            return;
        }

        byte* buffer = stackalloc byte[256];
        nint result = OpenSsl.X509_NAME_oneline(name, buffer, 256);
        PeerSubject = result == 0 ? null : Marshal.PtrToStringUTF8((nint)buffer);
    }

    internal TlsSession(nint ssl, nint rbio, nint wbio)
    {
        _ssl = ssl;
        _rbio = rbio;
        _wbio = wbio;
    }

    internal void AttachHandle(GCHandle handle) => _handle = handle;

    internal void AttachFd(int fd) => _fd = fd;

    internal void MarkTxEnabled(int fd)
    {
        _fd = fd;
        _txEnabled = true;
    }

    /// <summary>
    /// Feed one ciphertext slice and return everything that decrypts. The returned span is valid
    /// until the next call. Empty spans are normal (partial record); check <see cref="Closed"/>.
    /// </summary>
    public ReadOnlySpan<byte> Decrypt(byte* ciphertext, int length)
    {
        // With kTLS RX the kernel already decrypted this; recv handed us plaintext. Feeding it to
        // OpenSSL would be feeding plaintext to a record parser.
        if (KernelRx)
        {
            return new ReadOnlySpan<byte>(ciphertext, length);
        }

        _plainLen = 0;
        OpenSsl.BIO_write(_rbio, ciphertext, length);
        DrainPending();
        return _plain.AsSpan(0, _plainLen);
    }

    /// <summary>Plaintext decrypted during the handshake's final flight, if any.</summary>
    public ReadOnlySpan<byte> DrainPlaintext() => _plain.AsSpan(0, _plainLen);

    /// <summary>
    /// Feed one ciphertext slice and decrypt straight into <paramref name="writer"/>, returning the
    /// plaintext byte count. Same contract as <see cref="Decrypt"/> otherwise: an empty result is
    /// normal for a partial record, and <see cref="Closed"/> reports a clean close_notify.
    /// </summary>
    /// <remarks>
    /// The point of this over <see cref="Decrypt"/> is one fewer copy. <see cref="Decrypt"/> has to
    /// land the plaintext in <c>_plain</c> and hand back a span, so a caller feeding a pipe copies
    /// it a second time; here OpenSSL writes into the pipe's own memory and there is no
    /// intermediate. Callers with nowhere better to put the bytes still want <see cref="Decrypt"/>.
    /// </remarks>
    public int DecryptInto(byte* ciphertext, int length, PipeWriter writer)
    {
        if (KernelRx)
        {
            new ReadOnlySpan<byte>(ciphertext, length).CopyTo(writer.GetSpan(length));
            writer.Advance(length);
            return length;
        }

        OpenSsl.BIO_write(_rbio, ciphertext, length);

        int total = 0;
        while (true)
        {
            // A TLS record carries at most 2^14 bytes of plaintext (RFC 8446 section 5.1), so a
            // larger request cannot be filled by one record and a smaller one only costs extra
            // SSL_read calls. This is the protocol's own bound, not a tuning knob.
            Span<byte> destination = writer.GetSpan(MaxRecordPlaintext);

            int n;
            fixed (byte* p = destination)
            {
                // Synchronous, so the pin lasts only for the call.
                n = OpenSsl.SSL_read(_ssl, p, destination.Length);
            }

            if (n > 0)
            {
                writer.Advance(n);
                total += n;
                continue;
            }

            if (!ShouldKeepReading(n))
            {
                return total;
            }
        }
    }

    /// <summary>Most plaintext one TLS record can carry (RFC 8446 section 5.1) - the protocol's
    /// own bound, not a tuning knob.</summary>
    private const int MaxRecordPlaintext = 16 * 1024;

    /// <summary>
    /// Classify a non-positive SSL_read. False means stop and wait for more ciphertext; a genuine
    /// protocol failure throws.
    /// </summary>
    /// <remarks>
    /// The distinction this draws used to be missing: every error except ZERO_RETURN was treated as
    /// "record incomplete", so a bad MAC or a malformed record was indistinguishable from needing
    /// more bytes. A corrupted stream then looked like a connection that had simply gone quiet, and
    /// a caller pumping a pipe would wait on it forever.
    /// </remarks>
    /// <summary>
    /// How many COMPLETE TLS records are pending in the read BIO, and whether anything is left over
    /// beyond them. Inspects the BIO's buffer through BIO_CTRL_INFO rather than consuming it.
    /// </summary>
    /// <remarks>
    /// This is what makes the kTLS RX handoff possible. Records the handshake already pulled off
    /// the socket are invisible to the kernel, so the sequence number it starts at has to account
    /// for them - and a PARTIAL record left behind cannot be accounted for at all, because those
    /// bytes are gone and the kernel will never see them. Then the only safe move is to stay in
    /// userspace for that connection.
    /// </remarks>
    internal int CountPendingRecords(out bool trailingPartial)
    {
        trailingPartial = false;

        byte* buffer;
        long available = OpenSsl.BIO_ctrl(_rbio, OpenSsl.BIO_CTRL_INFO, 0, &buffer);
        if (available <= 0 || buffer == null)
        {
            return 0;
        }

        int records = 0;
        long offset = 0;

        while (offset + 5 <= available)
        {
            // TLSCiphertext: type(1) legacy_version(2) length(2), then `length` encrypted bytes.
            int length = (buffer[offset + 3] << 8) | buffer[offset + 4];
            if (offset + 5 + length > available)
            {
                break;   // the record is not all here
            }

            records++;
            offset += 5 + length;
        }

        trailingPartial = offset < available;
        return records;
    }

    /// <summary>
    /// Stage <paramref name="plaintext"/> into the connection's write slab, correctly for whichever
    /// backend this session ended up with: under kTLS TX it goes in as-is and the kernel makes the
    /// records on send; otherwise OpenSSL encrypts here and the records go in. The one write call
    /// that is right in both modes - handlers should not branch on <see cref="KernelTx"/>.
    /// </summary>
    /// <remarks>
    /// The OpenSSL branch is where the extra copy the kTLS write path avoids actually lives:
    /// OpenSSL encrypts into its write BIO first, and BIO_read then moves the records into the
    /// slab. Two passes over the bytes instead of one - though BIO_read at least writes directly
    /// into the slab rather than through an intermediate of ours.
    /// </remarks>
    public void Write(TcpConnection connection, ReadOnlySpan<byte> plaintext)
    {
        // Empty is a no-op either way. It must not reach SSL_write: zero-length is an error there,
        // not a no-op, and a fixed over an empty span hands it a null pointer to boot.
        if (plaintext.IsEmpty)
        {
            return;
        }

        // With kTLS the kernel makes the records, so the plaintext goes straight into the slab;
        // without it OpenSSL has to encrypt first. A handler that picks the wrong one either sends
        // cleartext or double-encrypts, and which is wrong depends on a flag it probably did not
        // set - so it should not have to pick.
        if (KernelTx)
        {
            connection.Write(plaintext);
            return;
        }

        WriteEncrypted(connection, plaintext);
    }

    /// <summary>Encrypt unconditionally. Only <see cref="TlsEncryptingPipeWriter"/> wants this -
    /// it is constructed solely when kTLS is off, so the mode check would always be true.</summary>
    internal void WriteEncrypted(TcpConnection connection, ReadOnlySpan<byte> plaintext)
    {
        fixed (byte* p = plaintext)
        {
            if (OpenSsl.SSL_write(_ssl, p, plaintext.Length) <= 0)
            {
                throw new IOException($"TLS encrypt failed: {OpenSsl.LastError()}");
            }
        }

        while (true)
        {
            int pending = (int)OpenSsl.BIO_ctrl_pending(_wbio);
            if (pending <= 0)
            {
                return;
            }

            int chunk = Math.Min(pending, 16 * 1024);
            Span<byte> destination = connection.GetSpan(chunk);

            int n;
            fixed (byte* d = destination)
            {
                n = OpenSsl.BIO_read(_wbio, d, chunk);
            }

            if (n <= 0)
            {
                return;
            }
            connection.Advance(n);
        }
    }

    private bool ShouldKeepReading(int result)
    {
        int err = OpenSsl.SSL_get_error(_ssl, result);

        switch (err)
        {
            case OpenSsl.SSL_ERROR_WANT_READ:
            case OpenSsl.SSL_ERROR_WANT_WRITE:
                return false;   // normal: the record is not complete yet

            case OpenSsl.SSL_ERROR_ZERO_RETURN:
                Closed = true;  // clean close_notify
                return false;

            default:
                throw new IOException($"TLS decrypt failed (error {err}): {OpenSsl.LastError()}");
        }
    }

    internal void DrainPending()
    {
        while (true)
        {
            if (_plain.Length - _plainLen < 4 * 1024)
            {
                if (_plain.Length >= MaxPlaintextBytes)
                {
                    throw new IOException($"TLS plaintext exceeds {MaxPlaintextBytes} bytes in one decrypt");
                }
                Array.Resize(ref _plain, Math.Min(_plain.Length * 2, MaxPlaintextBytes));
            }

            int n;
            fixed (byte* p = _plain)
            {
                n = OpenSsl.SSL_read(_ssl, p + _plainLen, _plain.Length - _plainLen);
            }

            if (n > 0)
            {
                _plainLen += n;
                continue;
            }

            if (!ShouldKeepReading(n))
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        // Clean server-side teardown: send close_notify so the peer can tell end-of-stream from a
        // truncation, whichever backend owns the write side. Skip if the peer already closed.
        if (!Closed && _fd >= 0)
        {
            if (_txEnabled)
            {
                Ktls.SendCloseNotify(_fd);
            }
            else
            {
                SendCloseNotifyOpenSsl();
            }
        }

        // The traffic secrets exist for the kTLS handoff; by teardown they are either programmed
        // into the kernel (and zeroed there) or were never needed. Do not leave them on the heap.
        ZeroSecrets();

        OpenSsl.SSL_free(_ssl);   // frees both BIOs
        if (_handle.IsAllocated)
        {
            _handle.Free();
        }
    }

    internal void ZeroSecrets()
    {
        if (ServerSecret is not null)
        {
            CryptographicOperations.ZeroMemory(ServerSecret);
            ServerSecret = null;
        }
        if (ClientSecret is not null)
        {
            CryptographicOperations.ZeroMemory(ClientSecret);
            ClientSecret = null;
        }
    }

    /// <summary>
    /// Best-effort close_notify when OpenSSL owns the write side: SSL_shutdown produces the record
    /// into the write BIO, and a plain send puts it on the wire. The mirror of
    /// <see cref="Ktls.SendCloseNotify"/> - teardown-time, non-fatal, the peer may already be gone.
    /// </summary>
    private void SendCloseNotifyOpenSsl()
    {
        if (OpenSsl.SSL_shutdown(_ssl) < 0)
        {
            return;
        }

        // A TLS 1.3 close_notify record is ~24 bytes: 5 header + 2 alert + 1 inner type + 16 tag.
        byte* record = stackalloc byte[64];
        int n = OpenSsl.BIO_read(_wbio, record, 64);
        if (n > 0)
        {
            _ = send(_fd, record, (nuint)n, MSG_NOSIGNAL);
        }
    }

    private const int MSG_NOSIGNAL = 0x4000;   // a dead peer must not SIGPIPE the process

    [DllImport("libc")]
    private static extern nint send(int sockfd, byte* buf, nuint len, int flags);
}
