using System.Text;
using ioxide;
using ioxide.http3;
using ioxide.ngtcp2;

namespace Ioxide.Tests;

/// <summary>
/// The pure-C# HTTP/3 implementation (ioxide.http3: frames + QPACK + Huffman, no native code)
/// served over the ngtcp2 QUIC engine, driven by the nghttp3-based test client - a genuine
/// cross-implementation check: nghttp3 encodes (Huffman'd literals, static refs), our parser
/// decodes; our encoder answers, nghttp3 decodes.
/// </summary>
internal static class Http3Tests
{
    public static void Register(Runner runner)
    {
        runner.Test("http3: pure-C# parser GET vs nghttp3 client (loopback)", () =>
        {
            (string certPath, string keyPath) = TestCert.Ensure();
            using var engine = new QuicEngine(certPath, keyPath, cidLength: 8);

            (_, int udpPort) = TestServer.StartDatagram(
                onDatagram: null,
                quicFactory: engine.CreateFactory(),
                quicHandle: static (_, conn) => new Http3Connection(conn).RunAsync(
                    static req => Http3Response.Text(
                        $"pure {Encoding.ASCII.GetString(req.Path.Span)} via {Encoding.ASCII.GetString(req.Method.Span)}")));

            using var client = new H3TestClient("127.0.0.1", udpPort);
            client.Connect();
            Assert.True(client.CompleteHandshake(timeoutMs: 5000), "handshake did not complete");

            (int status, string body) = client.Get("/pure-cs", timeoutMs: 5000);
            Assert.Equal(200, status);
            Assert.Equal("pure /pure-cs via GET", body);
        });

        runner.Test("http3: pure-C# streaming body upload (paced window)", () =>
        {
            (string certPath, string keyPath) = TestCert.Ensure();
            using var engine = new QuicEngine(certPath, keyPath, cidLength: 8);

            (_, int udpPort) = TestServer.StartDatagram(
                onDatagram: null,
                quicFactory: engine.CreateFactory(),
                quicHandle: static (_, conn) => new Http3Connection(conn).RunAsync(
                    static async req =>
                    {
                        long total = 0;
                        while (true)
                        {
                            ReadOnlyMemory<byte> chunk = await req.BodyReader!.ReadAsync();
                            if (chunk.IsEmpty)
                            {
                                break;
                            }
                            total += chunk.Length;
                        }
                        return Http3Response.Text($"pure got {total}");
                    }));

            using var client = new H3TestClient("127.0.0.1", udpPort);
            client.Connect();
            Assert.True(client.CompleteHandshake(timeoutMs: 5000), "handshake did not complete");

            // 600 KB > the 256 KB stream window: completes only if the parser's immediate credits
            // + sink hand-out credits keep the peer's window opening mid-flight.
            var body = new byte[600_000];
            new Random(11).NextBytes(body);

            (int status, string text) = client.Request("POST", "/upload", body, timeoutMs: 10_000);
            Assert.Equal(200, status);
            Assert.Equal("pure got 600000", text);
        });
    }
}
