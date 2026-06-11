namespace ioxide.tls;

/// <summary>
/// The receive half of a TLS connection after the kTLS TX handoff: inbound bytes are still TLS
/// records, decrypted here through OpenSSL. One session per connection, reactor-thread only.
/// </summary>
public sealed unsafe class TlsSession : IDisposable
{
    private readonly nint _ssl;
    private readonly nint _rbio;
    private byte[] _plain = new byte[16 * 1024];
    private int _plainLen;
    private bool _disposed;

    /// <summary>True once the peer sent close_notify.</summary>
    public bool Closed { get; private set; }

    internal TlsSession(nint ssl, nint rbio)
    {
        _ssl = ssl;
        _rbio = rbio;
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

    internal void DrainPending()
    {
        while (true)
        {
            if (_plain.Length - _plainLen < 4 * 1024)
            {
                Array.Resize(ref _plain, _plain.Length * 2);
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

            int err = OpenSsl.SSL_get_error(_ssl, n);
            if (err == OpenSsl.SSL_ERROR_ZERO_RETURN)
            {
                Closed = true;   // clean close_notify
            }
            return;              // WANT_READ: record incomplete, wait for more bytes
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        OpenSsl.SSL_free(_ssl);   // frees both BIOs
    }
}
