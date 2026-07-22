using ioxide.tls;

namespace Ioxide.E2E;

/// <summary>TLS: OpenSSL handshake over the ring, then kTLS-encrypted plaintext writes.</summary>
internal static class TlsTests
{
    public static void Register(Runner runner, bool ktls)
    {
        runner.Test("tls: kTLS handshake + request", () =>
        {
            (string certPath, string keyPath) = TestCert.Ensure();
            var options = new TlsOptions { CertificatePath = certPath, KeyPath = keyPath };
            int port = TestServer.Start(Handlers.Tls, r => TlsService.Start(r, options));
            (int status, string body) = Client.GetTls(port, "/");
            Assert.Equal(200, status);
            Assert.Equal("tls-ok", body);
        }, skip: !ktls);
    }
}
