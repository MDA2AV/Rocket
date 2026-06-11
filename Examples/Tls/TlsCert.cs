using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Examples.Tls;

/// <summary>
/// A self-signed cert for the TLS examples, so they run with no external files. Generated once and
/// written to PEM (ioxide.tls wants file paths; SslStream loads the same pair). Override with
/// EXAMPLES_TLS_CERT / EXAMPLES_TLS_KEY to use your own.
/// </summary>
public static class TlsCert
{
    public static string CertPath { get; private set; } = "";
    public static string KeyPath { get; private set; } = "";

    public static X509Certificate2 EnsureCert()
    {
        string? envCert = Environment.GetEnvironmentVariable("EXAMPLES_TLS_CERT");
        string? envKey = Environment.GetEnvironmentVariable("EXAMPLES_TLS_KEY");
        if (!string.IsNullOrEmpty(envCert) && !string.IsNullOrEmpty(envKey))
        {
            CertPath = envCert;
            KeyPath = envKey;
            return X509Certificate2.CreateFromPemFile(CertPath, KeyPath);
        }

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using X509Certificate2 cert = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        string dir = Path.Combine(Path.GetTempPath(), "ioxide-examples-tls");
        Directory.CreateDirectory(dir);
        CertPath = Path.Combine(dir, "server.crt");
        KeyPath = Path.Combine(dir, "server.key");
        File.WriteAllText(CertPath, cert.ExportCertificatePem());
        File.WriteAllText(KeyPath, rsa.ExportPkcs8PrivateKeyPem());

        // Reload from PEM so SslStream gets a clean, usable private key on Linux.
        return X509Certificate2.CreateFromPemFile(CertPath, KeyPath);
    }
}
