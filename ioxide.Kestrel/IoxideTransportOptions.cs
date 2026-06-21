using ioxide;

namespace ioxide.Kestrel;

/// <summary>Options for the ioxide Kestrel transport.</summary>
public sealed class IoxideTransportOptions
{
    /// <summary>
    /// Number of ioxide reactors (one io_uring ring + one dedicated thread each), load-balanced by
    /// SO_REUSEPORT. Defaults to the processor count.
    /// </summary>
    public int ReactorCount { get; set; } = Environment.ProcessorCount;

    /// <summary>
    /// Optional hook to customize the ioxide <see cref="ServerConfig"/> (ring depth, recv buffer ring,
    /// write slab, zero-copy send, incremental mode, ...). The listen port and reactor count are always
    /// overridden afterwards by the bound endpoint and <see cref="ReactorCount"/>.
    /// </summary>
    public Func<ServerConfig, ServerConfig>? ConfigureServer { get; set; }

    /// <summary>
    /// TLS termination via kTLS (kernel TLS), done in the transport on the listed ports. When set, the
    /// reactor runs the TLS 1.3 handshake on accept, installs kTLS TX, and hands Kestrel a plaintext
    /// connection with the TLS connection features set — so the endpoint must NOT use <c>UseHttps()</c>.
    /// Null = no TLS (every port is plaintext). Currently HTTP/1.1 only (see <see cref="IoxideTlsOptions.Alpn"/>).
    /// </summary>
    public IoxideTlsOptions? Tls { get; set; }

    /// <summary>
    /// Convenience over assigning <see cref="Tls"/>: terminate kTLS on <paramref name="ports"/> with one
    /// certificate/key. Example: <c>o.UseTls("/certs/server.crt", "/certs/server.key", new[] { 8081 });</c>.
    /// </summary>
    public void UseTls(string certificatePath, string keyPath, IEnumerable<int> ports, string alpn = "http/1.1")
    {
        var tls = new IoxideTlsOptions
        {
            CertificatePath = certificatePath,
            KeyPath = keyPath,
            Alpn = alpn,
        };
        foreach (var p in ports)
        {
            tls.Ports.Add(p);
        }
        Tls = tls;
    }
}

/// <summary>kTLS termination settings for the ioxide Kestrel transport.</summary>
public sealed class IoxideTlsOptions
{
    /// <summary>PEM certificate chain file.</summary>
    public required string CertificatePath { get; set; }

    /// <summary>PEM private key file.</summary>
    public required string KeyPath { get; set; }

    /// <summary>The single ALPN protocol to advertise/select (HTTP/1.1 for now). HTTP/2-over-TLS needs
    /// dynamic ALPN, which is not yet exposed by ioxide.tls.</summary>
    public string Alpn { get; set; } = "http/1.1";

    /// <summary>Listen ports that terminate TLS in the transport. Connections on other ports stay plaintext.</summary>
    public HashSet<int> Ports { get; set; } = new();
}
