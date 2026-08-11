using System.Text;
using ioxide;
using ioxide.nghttp3;
using ioxide.ngtcp2;

namespace Ioxide.Tests;

/// <summary>
/// Client certificates over QUIC. Three things have to hold together or the feature is theatre: a
/// certificate the server trusts gets in, one it does not is turned away, and the handler can say
/// WHICH client it got - a server that only knows "some valid certificate" has a gate, not an
/// identity.
/// </summary>
internal static class MutualTlsTests
{
    public static void Register(Runner runner)
    {
        runner.Test("mtls: a client with a trusted certificate connects, and is named", () =>
        {
            (string ca, string serverCert, string serverKey,
             string clientCert, string clientKey, _, _) = TestCert.EnsureMutualTls();

            using var engine = new QuicEngine(serverCert, serverKey, cidLength: 8, alpn: ["h3"],
                clientCaPemPath: ca, requireClientCertificate: true);

            (_, int udpPort) = TestServer.StartDatagram(
                onDatagram: null,
                quicFactory: engine.CreateFactory(),
                // Read inside the REQUEST handler, not at accept: the connection callback fires
                // before the handshake finishes, so the identity does not exist yet there.
                quicHandle: static (_, conn) => new Nghttp3Connection(conn).RunBufferedAsync(
                    _ => new Nghttp3Response
                    {
                        Body = Encoding.ASCII.GetBytes((conn as QuicEngineConnection)?.PeerSubject ?? "anonymous"),
                    }));

            using var client = new H3TestClient("127.0.0.1", udpPort, clientCert, clientKey);
            client.Connect();
            Assert.True(client.CompleteHandshake(timeoutMs: 5000), "handshake did not complete");

            (int status, string body) = client.Get("/", timeoutMs: 5000);
            Assert.Equal(200, status);
            Assert.True(body.Contains("alice"), $"the handler should see the client's subject, got: {body}");
        });

        runner.Test("mtls: a client with no certificate is refused", () =>
        {
            (string ca, string serverCert, string serverKey, _, _, _, _) = TestCert.EnsureMutualTls();

            using var engine = new QuicEngine(serverCert, serverKey, cidLength: 8, alpn: ["h3"],
                clientCaPemPath: ca, requireClientCertificate: true);

            (_, int udpPort) = TestServer.StartDatagram(
                onDatagram: null,
                quicFactory: engine.CreateFactory(),
                quicHandle: static (_, conn) => new Nghttp3Connection(conn).RunBufferedAsync(
                    static _ => new Nghttp3Response { Body = "should never be reached"u8.ToArray() }));

            // The stock client offers nothing, which is the whole point of requiring one.
            //
            // Asserted on the REQUEST, not the handshake: in TLS 1.3 the client finishes its own
            // side before it can learn the server rejected its certificate, so the client thinking
            // it is established proves nothing. Being answered does.
            using var client = new H3TestClient("127.0.0.1", udpPort);
            client.Connect();
            client.CompleteHandshake(timeoutMs: 3000);

            (int status, _) = client.Get("/", timeoutMs: 3000);
            Assert.True(status != 200, $"a client with no certificate was served anyway (status {status})");
        });

        runner.Test("mtls: a certificate from another CA is refused", () =>
        {
            (string ca, string serverCert, string serverKey, _, _,
             string rogueCert, string rogueKey) = TestCert.EnsureMutualTls();

            using var engine = new QuicEngine(serverCert, serverKey, cidLength: 8, alpn: ["h3"],
                clientCaPemPath: ca, requireClientCertificate: true);

            (_, int udpPort) = TestServer.StartDatagram(
                onDatagram: null,
                quicFactory: engine.CreateFactory(),
                quicHandle: static (_, conn) => new Nghttp3Connection(conn).RunBufferedAsync(
                    static _ => new Nghttp3Response { Body = "should never be reached"u8.ToArray() }));

            // Well-formed and correctly signed - by a CA this server has never heard of.
            using var client = new H3TestClient("127.0.0.1", udpPort, rogueCert, rogueKey);
            client.Connect();
            client.CompleteHandshake(timeoutMs: 3000);

            (int status, _) = client.Get("/", timeoutMs: 3000);
            Assert.True(status != 200, $"an untrusted certificate was served anyway (status {status})");
        });

        runner.Test("mtls: off by default - no client CA means no certificate is asked for", () =>
        {
            // The regression guard for everyone not using mTLS: the handshake must be untouched.
            (string certPath, string keyPath) = TestCert.Ensure();
            using var engine = new QuicEngine(certPath, keyPath, cidLength: 8, alpn: ["h3"]);

            (_, int udpPort) = TestServer.StartDatagram(
                onDatagram: null,
                quicFactory: engine.CreateFactory(),
                // Read inside the REQUEST handler, not at accept: the connection callback fires
                // before the handshake finishes, so the identity does not exist yet there.
                quicHandle: static (_, conn) => new Nghttp3Connection(conn).RunBufferedAsync(
                    _ => new Nghttp3Response
                    {
                        Body = Encoding.ASCII.GetBytes((conn as QuicEngineConnection)?.PeerSubject ?? "anonymous"),
                    }));

            using var client = new H3TestClient("127.0.0.1", udpPort);
            client.Connect();
            Assert.True(client.CompleteHandshake(timeoutMs: 5000), "handshake did not complete");

            (int status, string body) = client.Get("/", timeoutMs: 5000);
            Assert.Equal(200, status);
            Assert.Equal("anonymous", body);
        });
    }
}
