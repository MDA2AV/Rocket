namespace ioxide.tls;

public sealed class TlsOptions
{
    /// <summary>PEM certificate chain file.</summary>
    public required string CertificatePath { get; init; }

    /// <summary>PEM private key file.</summary>
    public required string KeyPath { get; init; }

    /// <summary>ALPN protocol to select when the client offers it.</summary>
    public string Alpn { get; init; } = "http/1.1";
}
