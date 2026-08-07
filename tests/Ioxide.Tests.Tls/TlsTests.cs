using ioxide.tls;

namespace Ioxide.Tests;

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

        runner.Test("tls raw: a fragmented request gets exactly one response", () =>
        {
            // The raw decrypt loop answers on "any plaintext arrived", not on "a request arrived".
            // TLS reassembly is fine either way - OpenSSL's BIO holds the partial record - but the
            // HTTP framing above it is the handler's job, and answering per decrypt means one
            // request split across recvs draws several responses.
            (string certPath, string keyPath) = TestCert.Ensure();
            var options = new TlsOptions { CertificatePath = certPath, KeyPath = keyPath };

            int port = TestServer.Start(Handlers.Tls, r => TlsService.Start(r, options));

            int responses = Client.CountTlsResponsesForSplitRequest(port, "/", chunk: 1);
            Assert.Equal(1, responses);
        }, skip: !ktls);

        runner.Test("tls raw: a request split across 3 TLS records still gets one response", () =>
        {
            (string certPath, string keyPath) = TestCert.Ensure();
            var options = new TlsOptions { CertificatePath = certPath, KeyPath = keyPath };

            int port = TestServer.Start(Handlers.Tls, r => TlsService.Start(r, options));

            int responses = Client.CountTlsResponsesForMultiRecordRequest(port, "/", records: 3);
            Assert.Equal(1, responses);
        }, skip: !ktls);
    }
}
