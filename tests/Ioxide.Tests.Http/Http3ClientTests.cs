using System.Text;
using ioxide;
using ioxide.httpclient;
using ioxide.nghttp3;
using ioxide.ngtcp2;

namespace Ioxide.Tests;

/// <summary>
/// The ring-native HTTP/3 client against our own HTTP/3 server: the client's QUIC connection is
/// opened on a reactor and runs on that reactor's ring, so the whole path - handshake, request
/// streams, multiplexed responses - stays there. The driver hits a plain TCP endpoint whose
/// handler calls the h3 origin.
///
/// Both transport shapes are covered: STANDALONE (no ServerConfig.Quic - the reactor opens an
/// ephemeral-port socket on first connect) and PROXY (the app already serves QUIC, so outbound
/// connections share that socket).
/// </summary>
internal static class Http3ClientTests
{
    public static void Register(Runner runner)
    {
        runner.Test("httpclient h3: GET over HTTP/3 against our own h3 server", () =>
        {
            (string certPath, string keyPath) = TestCert.Ensure();
            using var serverEngine = new QuicEngine(certPath, keyPath, cidLength: 8, alpn: ["h3"]);

            (_, int originUdpPort) = TestServer.StartDatagram(
                onDatagram: null,
                quicFactory: serverEngine.CreateFactory(),
                quicHandle: static (_, connection) => new Nghttp3Connection(connection).RunBufferedAsync(
                    static request => Nghttp3Response.Text(
                        $"h3 origin saw {Encoding.ASCII.GetString(request.Path.Span)}")));

            int driver = StartH3Driver(originUdpPort);

            (int status, string body) = Client.Get(driver, "/from-h3-client");
            Assert.Equal(200, status);
            Assert.Equal("200|h3 origin saw /from-h3-client", body);
        });

        runner.Test("httpclient h3: many requests multiplex over one QUIC connection", () =>
        {
            (string certPath, string keyPath) = TestCert.Ensure();
            using var serverEngine = new QuicEngine(certPath, keyPath, cidLength: 8, alpn: ["h3"]);

            (_, int originUdpPort) = TestServer.StartDatagram(
                onDatagram: null,
                quicFactory: serverEngine.CreateFactory(),
                quicHandle: static (_, connection) => new Nghttp3Connection(connection).RunBufferedAsync(
                    static request => Nghttp3Response.Text(
                        Encoding.ASCII.GetString(request.Path.Span))));

            int driver = StartH3Driver(originUdpPort);

            // Each of these is a fresh request stream on the SAME QUIC connection (pool size 1).
            for (int i = 0; i < 25; i++)
            {
                (int status, string body) = Client.Get(driver, $"/req-{i}");
                Assert.Equal(200, status);
                Assert.Equal($"200|/req-{i}", body);
            }

            (_, string stats) = Client.Get(driver, "/connstats");
            Assert.Equal("conns=1", stats);
        });

        runner.Test("httpclient h3: outbound requests share a reactor that also serves QUIC", () =>
        {
            (string certPath, string keyPath) = TestCert.Ensure();
            using var originEngine = new QuicEngine(certPath, keyPath, cidLength: 8, alpn: ["h3"]);

            (_, int originUdpPort) = TestServer.StartDatagram(
                onDatagram: null,
                quicFactory: originEngine.CreateFactory(),
                quicHandle: static (_, connection) => new Nghttp3Connection(connection).RunBufferedAsync(
                    static _ => Nghttp3Response.Text("origin via shared socket")));

            // The driver's reactor SERVES h3 too, so QuicEnsureClientTransport takes the proxy path
            // and the outbound connection rides the configured socket instead of opening its own.
            // Both directions then demux by DCID on one fd.
            using var driverEngine = new QuicEngine(certPath, keyPath, cidLength: 8, alpn: ["h3"]);
            var sharedOptions = new Http3ClientOptions
            {
                Host = "127.0.0.1",
                Port = (ushort)originUdpPort,
                ServerName = "localhost",
                PoolSize = 1,
            };

            int sharedDriver = TestServer.StartQuicServingH3Driver(
                DriverHandler, driverEngine.CreateFactory(),
                onStart: reactor => Http3ClientPool.Start(reactor, sharedOptions));

            (int status, string body) = Client.Get(sharedDriver, "/shared");
            Assert.Equal(200, status);
            Assert.Equal("200|origin via shared socket", body);
        });

        runner.Test("httpclient h3: a response past MaxResponseBytes fails the request, not the process", () =>
        {
            // The arena refuses the bytes from inside an nghttp3 callback, where throwing would
            // cross native frames and kill the process. It records instead, and the connection
            // turns that into a failed request after nghttp3 unwinds - so what the caller sees is
            // an ordinary exception, and the reactor is still alive to serve the next request.
            (string certPath, string keyPath) = TestCert.Ensure();
            using var serverEngine = new QuicEngine(certPath, keyPath, cidLength: 8, alpn: ["h3"]);

            (_, int originUdpPort) = TestServer.StartDatagram(
                onDatagram: null,
                quicFactory: serverEngine.CreateFactory(),
                quicHandle: static (_, connection) => new Nghttp3Connection(connection).RunBufferedAsync(
                    static request => Nghttp3Response.Text(
                        request.Path.Span.SequenceEqual("/small"u8)
                            ? "tiny"
                            : new string('x', 64 * 1024))));

            int driver = StartH3Driver(originUdpPort, maxResponseBytes: 8 * 1024);

            (int status, string body) = Client.Get(driver, "/huge");
            Assert.Equal(200, status);                       // the driver answered at all
            Assert.True(body.StartsWith("599|"), $"request should have failed, got: {body}");
            Assert.True(body.Contains("MaxResponseBytes"), $"should name the cap, got: {body}");

            // The connection and its reactor survived, so a normal request still works.
            (_, string after) = Client.Get(driver, "/small");
            Assert.Equal("200|tiny", after);
        });
    }

    // A TCP endpoint whose handler forwards to the h3 origin through the ring-native h3 client.
    // Its reactor has NO QUIC configuration: the first outbound connect opens an ephemeral-port
    // socket for it, which is what makes ioxide.httpclient usable without running a server.
    private static int StartH3Driver(int originUdpPort, int maxResponseBytes = 8 * 1024 * 1024)
    {
        var options = new Http3ClientOptions
        {
            Host = "127.0.0.1",
            Port = (ushort)originUdpPort,
            ServerName = "localhost",
            PoolSize = 1,
            MaxResponseBytes = maxResponseBytes,
        };

        return TestServer.StartQuicClientHost(
            DriverHandler,
            onStart: reactor => Http3ClientPool.Start(reactor, options));
    }

    private static async Task DriverHandler(Reactor reactor, TcpConnection connection)
    {
        try
        {
            Http3ClientPool upstream = reactor.GetService<Http3ClientPool>()!;

            while (true)
            {
                RecvSnapshot snapshot = await connection.ReadAsync();
                string path = Wire.ReadPath(connection, snapshot);

                if (path == "/connstats")
                {
                    Wire.Write(connection, 200, $"conns={upstream.ConnectionCount}");
                }
                else
                {
                    string detail;
                    int status;
                    try
                    {
                        using HttpClientResponse response = await upstream.GetAsync(path);
                        status = response.Status;
                        detail = Encoding.ASCII.GetString(response.Body.Span);
                    }
                    catch (Exception e)
                    {
                        status = 599;
                        detail = e.Message;
                    }

                    Wire.Write(connection, 200, $"{status}|{detail}");
                }

                await connection.FlushAsync();

                if (snapshot.IsClosed)
                {
                    return;
                }
                connection.ResetRead();
            }
        }
        finally
        {
            connection.DecRef();
        }
    }
}
