using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;

namespace ioxide.Kestrel;

/// <summary>
/// The TLS connection features Kestrel reads when a connection is already TLS-terminated by the transport
/// (kTLS via ioxide.tls) instead of by <c>UseHttps()</c>/SslStream. Setting these on the
/// <see cref="IoxideConnectionContext"/> makes Kestrel treat the connection as HTTPS (scheme, IsHttps) and
/// pick the application protocol from ALPN. Values are fixed by ioxide.tls (TLS 1.3, AES-128-GCM-SHA256,
/// no client certificates / mTLS).
/// </summary>
internal sealed class IoxideTlsFeature : ITlsConnectionFeature, ITlsHandshakeFeature, ITlsApplicationProtocolFeature
{
    public IoxideTlsFeature(ReadOnlyMemory<byte> applicationProtocol)
    {
        ApplicationProtocol = applicationProtocol;
    }

    // ITlsApplicationProtocolFeature — the negotiated ALPN (HTTP/1.1 in Phase 1).
    public ReadOnlyMemory<byte> ApplicationProtocol { get; }

    // ITlsConnectionFeature — ioxide.tls never requests a client certificate.
    public X509Certificate2? ClientCertificate { get; set; }

    public Task<X509Certificate2?> GetClientCertificateAsync(CancellationToken cancellationToken)
        => Task.FromResult(ClientCertificate);

    // ITlsHandshakeFeature — fixed by ioxide.tls's single TLS 1.3 ciphersuite. NegotiatedCipherSuite is the
    // modern accessor; the legacy CipherAlgorithm/HashAlgorithm/KeyExchangeAlgorithm (+ *Strength) properties
    // are obsolete (SYSLIB0058) but still required interface members, so implement and suppress the warning.
    public SslProtocols Protocol => SslProtocols.Tls13;
    public TlsCipherSuite NegotiatedCipherSuite => TlsCipherSuite.TLS_AES_128_GCM_SHA256;
    public string HostName => string.Empty;
#pragma warning disable SYSLIB0058 // legacy ITlsHandshakeFeature cipher properties are obsolete but required
    public CipherAlgorithmType CipherAlgorithm => CipherAlgorithmType.Aes128;
    public int CipherStrength => 128;
    public HashAlgorithmType HashAlgorithm => HashAlgorithmType.Sha256;
    public int HashStrength => 256;
    public ExchangeAlgorithmType KeyExchangeAlgorithm => ExchangeAlgorithmType.None;
    public int KeyExchangeStrength => 0;
#pragma warning restore SYSLIB0058
}
