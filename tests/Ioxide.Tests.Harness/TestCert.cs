using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Ioxide.Tests;

/// <summary>A throwaway self-signed cert for the TLS test, written to PEM (ioxide.tls wants paths).</summary>
public static class TestCert
{
    // ---- fixture plumbing -----------------------------------------------------------------------
    //
    // Five suites share these directories and nothing stopped two of them minting the same fixture
    // at the same time. Two processes interleaving their writes to ca.crt and ca.key leaves a
    // certificate from one generation beside a key from another: every handshake using them fails,
    // and it looks like a bug in whatever was under test rather than in the fixture. Writes were
    // not atomic either, so a reader could see a half-written PEM. And the cache never expired,
    // while leaves are minted for a year - so a checkout older than that fails every positive test
    // and passes every negative one, which is the worse direction.

    /// <summary>
    /// Serialises fixture creation across processes. Held for the whole check-and-mint, so the
    /// check cannot be won by one process while another is still writing the files it is checking.
    /// </summary>
    private static FileStream Lock(string dir, string name)
    {
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $".{name}.lock");

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (attempt < 1_200)
            {
                Thread.Sleep(50);   // another suite is minting the same fixture
            }
        }
    }

    /// <summary>Publishes a file in one step, so no reader ever sees a partial PEM.</summary>
    private static void WriteAtomic(string path, string contents)
    {
        string temp = $"{path}.{Environment.ProcessId}.tmp";
        File.WriteAllText(temp, contents);
        File.Move(temp, path, overwrite: true);   // same directory, so this is a rename
    }

    /// <summary>
    /// Whether a cached fixture can still be used: every file present, and the certificate inside
    /// its validity window. Expiry matters because these are cached indefinitely - without the
    /// check, a machine that ran the suite over a year ago fails every test that expects a
    /// handshake to succeed, and passes every test that expects one to fail.
    /// </summary>
    private static bool Fresh(string certPath, params string[] alsoRequired)
    {
        if (!File.Exists(certPath) || alsoRequired.Any(path => !File.Exists(path)))
        {
            return false;
        }

        try
        {
            using X509Certificate2 cert = X509CertificateLoader.LoadCertificateFromFile(certPath);
            DateTime now = DateTime.Now;
            return cert.NotBefore <= now && now < cert.NotAfter;
        }
        catch
        {
            return false;   // truncated or not a certificate at all: mint it again
        }
    }

    /// <summary>
    /// A CA, a server certificate and two client certificates - one the CA signed, one a DIFFERENT
    /// CA signed. mTLS cannot be tested without all four: proving a good certificate is let in says
    /// nothing unless a bad one is turned away.
    /// </summary>
    public static (string CaPath, string ServerCert, string ServerKey,
                   string ClientCert, string ClientKey,
                   string RogueCert, string RogueKey) EnsureMutualTls()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ioxide-e2e-mtls");
        Directory.CreateDirectory(dir);

        string ca = Path.Combine(dir, "ca.crt");
        string serverCert = Path.Combine(dir, "server.crt");
        string serverKey = Path.Combine(dir, "server.key");
        string clientCert = Path.Combine(dir, "client.crt");
        string clientKey = Path.Combine(dir, "client.key");
        string rogueCert = Path.Combine(dir, "rogue.crt");
        string rogueKey = Path.Combine(dir, "rogue.key");

        using FileStream guard = Lock(dir, "mtls");

        // Every file, not a sample of them. The old check tested four of the eight, so a partially
        // evicted cache returned paths to files that were not there.
        if (Fresh(serverCert, ca, Path.Combine(dir, "ca.key"), serverKey, clientCert, clientKey, rogueCert, rogueKey))
        {
            return (ca, serverCert, serverKey, clientCert, clientKey, rogueCert, rogueKey);
        }

        // ONE window for the whole set. Taking UtcNow per certificate lets a leaf be issued a
        // second after its CA and therefore outlive it, which .NET refuses outright - a flake that
        // only fires when the two calls straddle a second boundary.
        DateTimeOffset notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        DateTimeOffset caNotAfter = notBefore.AddYears(2);
        DateTimeOffset leafNotAfter = notBefore.AddYears(1);   // strictly inside the CA's window

        using var caKey = System.Security.Cryptography.RSA.Create(2048);
        var caRequest = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=ioxide test CA", caKey, System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(
            new System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension(true, false, 0, true));
        using var caCert = caRequest.CreateSelfSigned(notBefore, caNotAfter);
        WriteAtomic(ca, caCert.ExportCertificatePem());

        // The CA's key too, so a test can issue a certificate for a name of its own later -
        // EnsureNamedFromCa signs with it, and a chain needs the issuer that a client will anchor to.
        WriteAtomic(Path.Combine(dir, "ca.key"), caKey.ExportPkcs8PrivateKeyPem());

        Sign(caCert, "CN=localhost", serverCert, serverKey, server: true, notBefore, leafNotAfter);
        Sign(caCert, "CN=alice", clientCert, clientKey, server: false, notBefore, leafNotAfter);

        // A second CA the server has never heard of, so its certificates must be refused.
        using var rogueKeyPair = System.Security.Cryptography.RSA.Create(2048);
        var rogueCa = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=rogue CA", rogueKeyPair, System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        rogueCa.CertificateExtensions.Add(
            new System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension(true, false, 0, true));
        using var rogueCaCert = rogueCa.CreateSelfSigned(notBefore, caNotAfter);
        Sign(rogueCaCert, "CN=mallory", rogueCert, rogueKey, server: false, notBefore, leafNotAfter);

        return (ca, serverCert, serverKey, clientCert, clientKey, rogueCert, rogueKey);

        static void Sign(System.Security.Cryptography.X509Certificates.X509Certificate2 issuer,
            string subject, string certPath, string keyPath, bool server,
            DateTimeOffset notBefore, DateTimeOffset notAfter)
        {
            using var key = System.Security.Cryptography.RSA.Create(2048);
            var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(
                subject, key, System.Security.Cryptography.HashAlgorithmName.SHA256,
                System.Security.Cryptography.RSASignaturePadding.Pkcs1);

            if (server)
            {
                var names = new System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder();
                names.AddDnsName("localhost");
                names.AddIpAddress(System.Net.IPAddress.Loopback);
                request.CertificateExtensions.Add(names.Build());
            }

            request.CertificateExtensions.Add(
                new System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension(
                    [new System.Security.Cryptography.Oid(server ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2")], false));

            byte[] serial = new byte[8];
            System.Security.Cryptography.RandomNumberGenerator.Fill(serial);
            using var signed = request.Create(issuer, notBefore, notAfter, serial);

            WriteAtomic(certPath, signed.ExportCertificatePem());
            WriteAtomic(keyPath, key.ExportPkcs8PrivateKeyPem());
        }
    }

    /// <summary>
    /// A self-signed certificate for one name, so SNI selection has something to tell apart. The
    /// subject carries the name, which is what a test asserts on after the handshake.
    /// </summary>
    public static (string CertPath, string KeyPath) EnsureNamed(string host)
    {
        string dir = Path.Combine(Path.GetTempPath(), "ioxide-e2e-tls");
        Directory.CreateDirectory(dir);

        // Distinct hosts must not share a filename. Substituting the awkward characters alone maps
        // alpha.test and alpha_test onto one pair of files, and whichever is asked for second
        // silently gets the other's certificate - a wrong-certificate result inside the very
        // fixture meant to prove certificates are chosen correctly. The hash keeps them apart.
        string safe = host.Replace('*', '_').Replace('.', '_');
        string tag = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(host)))[..8];

        string certPath = Path.Combine(dir, $"sni-{safe}-{tag}.crt");
        string keyPath = Path.Combine(dir, $"sni-{safe}-{tag}.key");

        // Both halves, not just the certificate: a leftover certificate beside a missing key is
        // reused forever and every test fails at startup on a path that looks perfectly fine.
        using FileStream guard = Lock(dir, "named");

        if (!Fresh(certPath, keyPath))
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest($"CN={host}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName(host);
            request.CertificateExtensions.Add(names.Build());

            using X509Certificate2 cert = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

            WriteAtomic(certPath, cert.ExportCertificatePem());
            WriteAtomic(keyPath, rsa.ExportPkcs8PrivateKeyPem());
        }

        return (certPath, keyPath);
    }

    /// <summary>
    /// A certificate for one name, signed by the same CA <see cref="EnsureMutualTls"/> mints, with
    /// that name as a SAN. Unlike <see cref="EnsureNamed"/>'s self-signed pair, this is something a
    /// REAL client can validate: a chain back to an anchor it can be given, and a name to match.
    /// </summary>
    /// <remarks>
    /// Which is the point. ioxide's own QUIC client authenticates nothing, so a test driven by it
    /// can only ever ask "which certificate came back". Handing these to a client that checks makes
    /// the assertion "would anyone actually accept this", and that is a different question.
    /// </remarks>
    /// <summary>
    /// A second, DIFFERENT certificate for the same name, signed by the same CA - what a renewal
    /// produces. Distinct key and serial, identical subject and SAN, so a validating client accepts
    /// either and cannot tell which it got except by looking.
    /// </summary>
    public static (string CaPath, string CertPath, string KeyPath) EnsureRenewedFromCa(string host)
        => EnsureNamedFromCa(host, "renewed");

    public static (string CaPath, string CertPath, string KeyPath) EnsureNamedFromCa(string host)
        => EnsureNamedFromCa(host, "");

    private static (string CaPath, string CertPath, string KeyPath) EnsureNamedFromCa(string host, string variant)
    {
        (string ca, _, _, _, _, _, _) = EnsureMutualTls();

        string dir = Path.Combine(Path.GetTempPath(), "ioxide-e2e-mtls");
        string tag = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(host + "\u0000" + variant)))[..8];

        string certPath = Path.Combine(dir, $"host-{tag}.crt");
        string keyPath = Path.Combine(dir, $"host-{tag}.key");

        using FileStream guard = Lock(dir, "from-ca");

        if (Fresh(certPath, keyPath))
        {
            return (ca, certPath, keyPath);
        }

        // The CA's own key is not kept, so the CA is re-derived here from the same material rather
        // than loaded - EnsureMutualTls writes only the certificate.
        using X509Certificate2 caCert = X509Certificate2.CreateFromPemFile(
            Path.Combine(dir, "ca.crt"), Path.Combine(dir, "ca.key"));

        DateTimeOffset notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        DateTimeOffset notAfter = notBefore.AddYears(1);

        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={host}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(host);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));

        byte[] serial = new byte[8];
        RandomNumberGenerator.Fill(serial);
        using X509Certificate2 signed = request.Create(caCert, notBefore, notAfter, serial);

        WriteAtomic(certPath, signed.ExportCertificatePem());
        WriteAtomic(keyPath, key.ExportPkcs8PrivateKeyPem());

        return (ca, certPath, keyPath);
    }

    /// <summary>
    /// A CLIENT certificate from the mutual-TLS CA with a subject of the caller's choosing, so a
    /// test can mint an identity that is legitimately issued and yet renders into something
    /// awkward - a name containing the very separator the rendered DN uses, for instance.
    /// </summary>
    /// <param name="subject">Full distinguished name, e.g. <c>O=Acme, CN=bob</c>.</param>
    public static (string CaPath, string CertPath, string KeyPath) EnsureClientCertFromCa(string subject)
    {
        (string ca, _, _, _, _, _, _) = EnsureMutualTls();

        string dir = Path.Combine(Path.GetTempPath(), "ioxide-e2e-mtls");
        string tag = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes("client\u0000" + subject)))[..8];

        string certPath = Path.Combine(dir, $"client-{tag}.crt");
        string keyPath = Path.Combine(dir, $"client-{tag}.key");

        using FileStream guard = Lock(dir, "from-ca");

        if (Fresh(certPath, keyPath))
        {
            return (ca, certPath, keyPath);
        }

        using X509Certificate2 caCert = X509Certificate2.CreateFromPemFile(
            Path.Combine(dir, "ca.crt"), Path.Combine(dir, "ca.key"));

        DateTimeOffset notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        DateTimeOffset notAfter = notBefore.AddYears(1);

        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.2")], false));   // client auth

        byte[] serial = new byte[8];
        RandomNumberGenerator.Fill(serial);
        using X509Certificate2 signed = request.Create(caCert, notBefore, notAfter, serial);

        WriteAtomic(certPath, signed.ExportCertificatePem());
        WriteAtomic(keyPath, key.ExportPkcs8PrivateKeyPem());

        return (ca, certPath, keyPath);
    }

    public static (string CertPath, string KeyPath) Ensure()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ioxide-e2e-tls");
        Directory.CreateDirectory(dir);

        string certPath = Path.Combine(dir, "test.crt");
        string keyPath = Path.Combine(dir, "test.key");

        using FileStream guard = Lock(dir, "default");

        // Both halves and the validity window: the old check tested only the certificate, so a
        // missing key was cached forever and every suite failed at startup on a path that looked
        // perfectly fine.
        if (!Fresh(certPath, keyPath))
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            // A subjectAltName, because client-side hostname verification checks that and only
            // falls back to the CN when no dNSName is present at all. Without it the certificate
            // is fine for a server that never gets verified and useless for testing a client that
            // does.
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName("localhost");
            names.AddIpAddress(System.Net.IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());

            using X509Certificate2 cert = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

            WriteAtomic(certPath, cert.ExportCertificatePem());
            WriteAtomic(keyPath, rsa.ExportPkcs8PrivateKeyPem());
        }

        return (certPath, keyPath);
    }
}
