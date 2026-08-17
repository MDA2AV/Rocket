using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Playground.Shared;

/// <summary>Self-signed localhost cert for the QUIC modes.</summary>
public static class QuicCert
{
    /// <summary>
    /// A self-signed certificate for one host name, generated on first use. The SNI samples serve
    /// several of these from one port, so each carries its own name in the subject and its SAN -
    /// which is how you can tell from the client which one you were given.
    /// </summary>
    public static (string CertPath, string KeyPath) EnsureNamed(string host)
    {
        string dir = Path.Combine(Path.GetTempPath(), "ioxide-playground-quic");
        Directory.CreateDirectory(dir);

        string safe = host.Replace('.', '_');
        string certPath = Path.Combine(dir, $"sni-{safe}.crt");
        string keyPath = Path.Combine(dir, $"sni-{safe}.key");

        if (File.Exists(certPath) && File.Exists(keyPath))
        {
            return (certPath, keyPath);
        }

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={host}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(host);
        request.CertificateExtensions.Add(names.Build());

        using X509Certificate2 cert = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        File.WriteAllText(certPath, cert.ExportCertificatePem());
        File.WriteAllText(keyPath, rsa.ExportPkcs8PrivateKeyPem());

        return (certPath, keyPath);
    }

    /// <summary>
    /// Returns the configured cert/key pair, generating a self-signed one under the temp directory
    /// when <c>PLAYGROUND_QUIC_CERT</c>/<c>_KEY</c> are not set.
    /// </summary>
    public static (string CertPath, string KeyPath) Ensure(string? configuredCert, string? configuredKey)
    {
        if (configuredCert is not null && configuredKey is not null)
        {
            return (configuredCert, configuredKey);
        }

        string dir = Path.Combine(Path.GetTempPath(), "ioxide-playground-quic");
        Directory.CreateDirectory(dir);
        string certPath = Path.Combine(dir, "quic.crt");
        string keyPath = Path.Combine(dir, "quic.key");

        if (!File.Exists(certPath))
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using X509Certificate2 cert = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

            File.WriteAllText(certPath, cert.ExportCertificatePem());
            File.WriteAllText(keyPath, rsa.ExportPkcs8PrivateKeyPem());
        }

        return (certPath, keyPath);
    }
}
