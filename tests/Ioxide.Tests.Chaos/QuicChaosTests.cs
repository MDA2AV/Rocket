using System.Net;
using System.Net.Sockets;
using ioxide;
using ioxide.nghttp3;
using ioxide.ngtcp2;

namespace Ioxide.Tests;

/// <summary>
/// QUIC transport chaos: junk sprayed at the UDP port a real ngtcp2 + nghttp3 server listens on. The
/// demux must drop what is not a routable QUIC packet without disturbing the transport - proven not
/// by an echo but by a full HTTP/3 handshake and request completing AFTER the flood. The engine and
/// its native shims survive being fed nonsense.
/// </summary>
internal static class QuicChaosTests
{
    private static void AssertH3Serves(int udpPort)
    {
        using var client = new H3TestClient("127.0.0.1", udpPort);
        client.Connect();
        Assert.True(client.CompleteHandshake(timeoutMs: 5000), "handshake did not complete after the flood");
        (int status, string body) = client.Get("/", timeoutMs: 5000);
        Assert.Equal(200, status);
        Assert.Equal("ok", body);
    }

    private static int StartH3(QuicEngine engine) => TestServer.StartDatagram(
        onDatagram: null,
        quicFactory: engine.CreateFactory(),
        quicHandle: static (_, conn) => new Nghttp3Connection(conn).RunBufferedAsync(
            static _ => Nghttp3Response.Text("ok"))).UdpPort;

    public static void Register(Runner runner)
    {
        runner.Test("quic: a garbage datagram flood doesn't stop h3 from serving", () =>
        {
            (string certPath, string keyPath) = TestCert.Ensure();
            using var engine = new QuicEngine(certPath, keyPath, cidLength: 8);
            int udpPort = StartH3(engine);

            using (var udp = new UdpClient())
            {
                var server = new IPEndPoint(IPAddress.Loopback, udpPort);
                var rng = new Random(9);
                // 300 datagrams at every awkward size: empty, tiny, and near-MTU noise.
                for (int i = 0; i < 300; i++)
                {
                    int size = i % 5 switch { 0 => 0, 1 => 1, 2 => 8, 3 => 64, _ => 1200 };
                    byte[] junk = new byte[size];
                    rng.NextBytes(junk);
                    udp.Send(junk, junk.Length, server);
                }
            }

            AssertH3Serves(udpPort);
        });

        runner.Test("quic: malformed QUIC packets are dropped, a real handshake still completes", () =>
        {
            (string certPath, string keyPath) = TestCert.Ensure();
            using var engine = new QuicEngine(certPath, keyPath, cidLength: 8);
            int udpPort = StartH3(engine);

            using (var udp = new UdpClient())
            {
                var server = new IPEndPoint(IPAddress.Loopback, udpPort);

                Span<byte> overlongDcid = stackalloc byte[27];
                overlongDcid.Clear();
                overlongDcid[0] = 0xC0;   // long header, fixed bit
                overlongDcid[5] = 21;     // DCID length 21 > RFC 9000 max of 20

                byte[][] malformed =
                [
                    [],                       // empty datagram
                    [0xC0],                   // long header, truncated at the first byte
                    [0x40, 1, 2, 3],          // short header shorter than a routable CID
                    overlongDcid.ToArray(),   // long header claiming an illegal DCID length
                ];

                foreach (byte[] packet in malformed)
                {
                    udp.Send(packet, packet.Length, server);
                }
            }

            AssertH3Serves(udpPort);
        });
    }
}
