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
internal static class QuicMutualTlsTests
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

        runner.Test("mtls: the trust anchors can be PEM text rather than a file", () =>
        {
            (string ca, string serverCert, string serverKey,
             string clientCert, string clientKey, _, _) = TestCert.EnsureMutualTls();

            // The same anchors as the first test, handed over as data instead of as a path - what a
            // host keeping its bundle in a secrets store holds, without writing it out to read back.
            using var engine = new QuicEngine(serverCert, serverKey, cidLength: 8, alpn: ["h3"],
                requireClientCertificate: true, clientCaPem: File.ReadAllText(ca));

            (_, int udpPort) = TestServer.StartDatagram(
                onDatagram: null,
                quicFactory: engine.CreateFactory(),
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

        runner.Test("mtls: PEM text anchors refuse a certificate from another CA", () =>
        {
            // The other half of the one above: anchors read from text have to turn a stranger away
            // as well as let the trusted one in. A store that verified nothing would pass that test
            // just as happily, so admitting alice proves only half of it.
            (string ca, string serverCert, string serverKey, _, _,
             string rogueCert, string rogueKey) = TestCert.EnsureMutualTls();

            using var engine = new QuicEngine(serverCert, serverKey, cidLength: 8, alpn: ["h3"],
                requireClientCertificate: true, clientCaPem: File.ReadAllText(ca));

            (_, int udpPort) = TestServer.StartDatagram(
                onDatagram: null,
                quicFactory: engine.CreateFactory(),
                quicHandle: static (_, conn) => new Nghttp3Connection(conn).RunBufferedAsync(
                    static _ => new Nghttp3Response { Body = "should never be reached"u8.ToArray() }));

            using var client = new H3TestClient("127.0.0.1", udpPort, rogueCert, rogueKey);
            client.Connect();
            client.CompleteHandshake(timeoutMs: 3000);

            (int status, _) = client.Get("/", timeoutMs: 3000);
            Assert.True(status != 200, $"an untrusted certificate was served anyway (status {status})");
        });

        runner.Test("mtls: naming both a CA file and CA text is refused", () =>
        {
            (string ca, string serverCert, string serverKey, _, _, _, _) = TestCert.EnsureMutualTls();

            // Two sources for one answer: silently picking either would make the other look applied.
            bool refused = false;

            try
            {
                using var engine = new QuicEngine(serverCert, serverKey, cidLength: 8, alpn: ["h3"],
                    clientCaPemPath: ca, requireClientCertificate: true, clientCaPem: File.ReadAllText(ca));
            }
            catch (ArgumentException)
            {
                refused = true;
            }

            Assert.True(refused, "two client CA sources should be refused, not quietly resolved to one");
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

        // Optional mTLS: anchors configured, but a client offering nothing is still let in. The
        // point of the pair below is that BOTH halves have to hold. Only refusing the empty case
        // is easy; still identifying the clients that DO have a certificate is what makes the
        // setting mean "optional" rather than "off".
        runner.Test("mtls optional: a client with a certificate is still asked, and is named", () =>
        {
            (string ca, string serverCert, string serverKey,
             string clientCert, string clientKey, _, _) = TestCert.EnsureMutualTls();

            using var engine = new QuicEngine(serverCert, serverKey, cidLength: 8, alpn: ["h3"],
                clientCaPemPath: ca, requireClientCertificate: false);

            (_, int udpPort) = TestServer.StartDatagram(
                onDatagram: null,
                quicFactory: engine.CreateFactory(),
                quicHandle: static (_, conn) => new Nghttp3Connection(conn).RunBufferedAsync(
                    _ => new Nghttp3Response
                    {
                        Body = Encoding.ASCII.GetBytes((conn as QuicEngineConnection)?.PeerCommonName ?? "anonymous"),
                    }));

            using var client = new H3TestClient("127.0.0.1", udpPort, clientCert, clientKey);
            client.Connect();
            Assert.True(client.CompleteHandshake(timeoutMs: 5000), "handshake did not complete");

            (int status, string body) = client.Get("/", timeoutMs: 5000);
            Assert.Equal(200, status);
            Assert.True(body.Contains("alice"),
                $"an optional-mTLS server must still ASK, or a client with a certificate is never "
                + $"identified and optional means never; got: {body}");
        });

        runner.Test("mtls optional: a client with no certificate still gets in, unnamed", () =>
        {
            (string ca, string serverCert, string serverKey, _, _, _, _) = TestCert.EnsureMutualTls();

            using var engine = new QuicEngine(serverCert, serverKey, cidLength: 8, alpn: ["h3"],
                clientCaPemPath: ca, requireClientCertificate: false);

            (_, int udpPort) = TestServer.StartDatagram(
                onDatagram: null,
                quicFactory: engine.CreateFactory(),
                quicHandle: static (_, conn) => new Nghttp3Connection(conn).RunBufferedAsync(
                    _ => new Nghttp3Response
                    {
                        Body = Encoding.ASCII.GetBytes((conn as QuicEngineConnection)?.PeerCommonName ?? "anonymous"),
                    }));

            using var client = new H3TestClient("127.0.0.1", udpPort);
            client.Connect();
            Assert.True(client.CompleteHandshake(timeoutMs: 5000), "handshake did not complete");

            (int status, string body) = client.Get("/", timeoutMs: 5000);
            Assert.Equal(200, status);
            Assert.Equal("anonymous", body);
        });

        runner.Test("mtls optional: a certificate from another CA is still refused", () =>
        {
            // The half that must NOT be relaxed. "Optional" forgives an empty answer and nothing
            // else - a certificate that was offered and does not validate is a refusal either way.
            (string ca, string serverCert, string serverKey, _, _,
             string rogueCert, string rogueKey) = TestCert.EnsureMutualTls();

            using var engine = new QuicEngine(serverCert, serverKey, cidLength: 8, alpn: ["h3"],
                clientCaPemPath: ca, requireClientCertificate: false);

            (_, int udpPort) = TestServer.StartDatagram(
                onDatagram: null,
                quicFactory: engine.CreateFactory(),
                quicHandle: static (_, conn) => new Nghttp3Connection(conn).RunBufferedAsync(
                    static _ => new Nghttp3Response { Body = "should never be reached"u8.ToArray() }));

            using var client = new H3TestClient("127.0.0.1", udpPort, rogueCert, rogueKey);
            client.Connect();
            client.CompleteHandshake(timeoutMs: 3000);

            (int status, _) = client.Get("/", timeoutMs: 3000);
            Assert.True(status != 200, $"an untrusted certificate was served anyway (status {status})");
        });

        runner.Test("mtls: a refused client is TOLD, not left waiting", () =>
        {
            // Without a CONNECTION_CLOSE the client learns nothing and sits until its own idle
            // timeout, which is indistinguishable from a server that has gone away.
            (string ca, string serverCert, string serverKey, _, _, _, _) = TestCert.EnsureMutualTls();

            using var engine = new QuicEngine(serverCert, serverKey, cidLength: 8, alpn: ["h3"],
                clientCaPemPath: ca, requireClientCertificate: true);

            (_, int udpPort) = TestServer.StartDatagram(
                onDatagram: null,
                quicFactory: engine.CreateFactory(),
                quicHandle: static (_, conn) => new Nghttp3Connection(conn).RunBufferedAsync(
                    static _ => new Nghttp3Response { Body = "should never be reached"u8.ToArray() }));

            using var client = new H3TestClient("127.0.0.1", udpPort);
            client.Connect();
            client.CompleteHandshake(timeoutMs: 3000);
            client.Get("/", timeoutMs: 3000);

            Assert.True(client.PeerClosed, "the server refused the handshake but never said so");
        });

        runner.Test("quic: a connection ID length the wire cannot carry is refused", () =>
        {
            // 20 bytes is the QUIC maximum and ngtcp2_cid is sized to exactly that, so a longer
            // one would be written past the end of a stack struct while minting the server's CID.
            (string certPath, string keyPath) = TestCert.Ensure();

            Assert.Throws<ArgumentOutOfRangeException>(
                () => new QuicEngine(certPath, keyPath, cidLength: 21).Dispose(),
                "1..20");

            Assert.Throws<ArgumentOutOfRangeException>(
                () => new QuicEngine(certPath, keyPath, cidLength: 0).Dispose(),
                "1..20");
        });
    }
}
