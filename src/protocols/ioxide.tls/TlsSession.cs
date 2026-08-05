using System.IO.Pipelines;
using System.Runtime.InteropServices;

namespace ioxide.tls;

/// <summary>
/// The receive half of a TLS connection after the kTLS TX handoff: inbound bytes are still TLS
/// records, decrypted here through OpenSSL. One session per connection, reactor-thread only.
/// </summary>
public sealed unsafe class TlsSession : IDisposable
{
    // Defensive cap on plaintext produced by a single Decrypt. In normal operation this is bounded
    // by the ciphertext slice fed per call (one recv buffer) - decryption never expands - so the cap
    // is a guard against a pathological stream, not the everyday bound.
    private const int MaxPlaintextBytes = 8 * 1024 * 1024;

    private readonly nint _ssl;
    private readonly nint _rbio;
    private byte[] _plain = new byte[16 * 1024];
    private int _plainLen;
    private bool _disposed;

    private GCHandle _handle;   // roots this session so the keylog callback can reach it via SSL ex_data
    private int _fd = -1;       // set once kTLS TX is enabled - the fd for the teardown close_notify
    private bool _txEnabled;

    /// <summary>The captured TLS 1.3 server traffic secret (set by the keylog callback during the handshake).</summary>
    internal byte[]? ServerSecret;

    /// <summary>True once the peer sent close_notify.</summary>
    public bool Closed { get; private set; }

    internal TlsSession(nint ssl, nint rbio)
    {
        _ssl = ssl;
        _rbio = rbio;
    }

    internal void AttachHandle(GCHandle handle) => _handle = handle;

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

    /// <summary>Maximum plaintext in one TLS record, RFC 8446 section 5.1.</summary>
    private const int MaxRecordPlaintext = 16 * 1024;

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
        // truncation. Skip if the peer already closed, or if kTLS TX was never enabled (no record path).
        if (_txEnabled && !Closed)
        {
            Ktls.SendCloseNotify(_fd);
        }

        OpenSsl.SSL_free(_ssl);   // frees both BIOs
        if (_handle.IsAllocated)
        {
            _handle.Free();
        }
    }
}
