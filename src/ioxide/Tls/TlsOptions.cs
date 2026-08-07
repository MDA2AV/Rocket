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
}
