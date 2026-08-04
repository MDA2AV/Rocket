using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Playground.Shared.Setup;

/// <summary>Self-signed localhost cert for the QUIC modes.</summary>
public static class QuicCert
{
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
