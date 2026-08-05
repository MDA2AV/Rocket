using System.Text;
using ioxide;
using ioxide.httpclient;
using ioxide.nghttp3;
using ioxide.ngtcp2;

namespace Ioxide.Tests;

/// <summary>
/// The unified client: one request/response type over either protocol, with the protocol chosen per
/// origin. Negotiation is Alt-Svc driven, because h1 (TCP) and h3 (QUIC) share no handshake to
/// negotiate within - the origin advertises h3 on an HTTP/1.1 response, and later requests use it.
/// </summary>
internal static class RingHttpClientTests
{
    public static void Register(Runner runner)
    {
        runner.Test("ringclient: starts on h1, upgrades to h3 after Alt-Svc", () =>
        {
            (string certPath, string keyPath) = TestCert.Ensure();
            using var engine = new QuicEngine(certPath, keyPath, cidLength: 8, alpn: ["h3"]);

            (_, int h3Port) = TestServer.StartDatagram(
                onDatagram: null,
                quicFactory: engine.CreateFactory(),
                quicHandle: static (_, connection) => new Nghttp3Connection(connection).RunBufferedAsync(
                    static _ => Nghttp3Response.Text("served by h3")));

            // The h1 origin advertises the h3 endpoint on every response.
            int h1Port = TestServer.Start(AdvertisingOriginHandler(h3Port));

            int driver = TestServer.Start(DriverHandler, onStart: reactor => RingHttpClient.Start(reactor,
                new RingHttpClientOptions { Host = "127.0.0.1", Port = (ushort)h1Port, ServerName = "localhost" }));

            // Before anything is sent, nothing has advertised h3 yet.
            (_, string before) = Client.Get(driver, "/protocol");
            Assert.Equal("http/1.1", before);

            // First request rides h1 and carries the advertisement back.
            (int status, string first) = Client.Get(driver, "/fetch");
            Assert.Equal(200, status);
            Assert.Equal("200|served by h1", first);

            (_, string after) = Client.Get(driver, "/protocol");
            Assert.Equal("h3", after);

            // The next request goes over h3 - a different origin process answers it.
            (int status2, string second) = Client.Get(driver, "/fetch");
            Assert.Equal(200, status2);
            Assert.Equal("200|served by h3", second);
        });

        runner.Test("ringclient: Http1Only ignores an Alt-Svc advertisement", () =>
        {
            int h1Port = TestServer.Start(AdvertisingOriginHandler(TestServer.DeadUdpPort()));

            int driver = TestServer.Start(DriverHandler, onStart: reactor => RingHttpClient.Start(reactor,
                new RingHttpClientOptions
                {
                    Host = "127.0.0.1",
                    Port = (ushort)h1Port,
                    Policy = HttpProtocolPolicy.Http1Only,
                }));

            (int status, string body) = Client.Get(driver, "/fetch");
            Assert.Equal(200, status);
            Assert.Equal("200|served by h1", body);

            (_, string protocol) = Client.Get(driver, "/protocol");
            Assert.Equal("http/1.1", protocol);   // advertisement seen and deliberately not followed
        });

        runner.Test("ringclient: a dead h3 endpoint falls back to h1 instead of failing", () =>
        {
            // A UDP port with nothing bound: derived rather than hardcoded, because a literal that
            // turns out to be live does not fail this test - it inverts it.
            int deadUdpPort = TestServer.DeadUdpPort();
            int h1Port = TestServer.Start(AdvertisingOriginHandler(deadUdpPort));

            // Http3Port is pinned at a port nothing listens on, so the h3 attempt cannot succeed.
            int driver = TestServer.Start(DriverHandler, onStart: reactor => RingHttpClient.Start(reactor,
                new RingHttpClientOptions
                {
                    Host = "127.0.0.1",
                    Port = (ushort)h1Port,
                    Http3Port = (ushort)deadUdpPort,
                    ServerName = "localhost",
                    AcquireTimeoutMs = 2000,
                }));

            (_, string protocol) = Client.Get(driver, "/protocol");
            Assert.Equal("h3", protocol);   // it will try h3 first

            // The request still succeeds, over h1, and the origin stays demoted afterwards.
            (int status, string body) = Client.Get(driver, "/fetch", timeoutMs: 20_000);
            Assert.Equal(200, status);
            Assert.Equal("200|served by h1", body);

            (_, string afterFailure) = Client.Get(driver, "/protocol");
            Assert.Equal("http/1.1", afterFailure);
        });
    }

    // An HTTP/1.1 origin that advertises an h3 endpoint on the same host.
    private static Func<Reactor, TcpConnection, Task> AdvertisingOriginHandler(int h3Port)
        => async (_, connection) =>
        {
            try
            {
                while (true)
                {
                    RecvSnapshot snapshot = await connection.ReadAsync();
                    Wire.ReadPath(connection, snapshot);   // path ignored: every path answers the same

                    const string body = "served by h1";
                    connection.Write(Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 200 OK\r\nAlt-Svc: h3=\":{h3Port}\"; ma=86400\r\n" +
                        $"Content-Length: {body.Length}\r\n\r\n{body}"));
                    await connection.FlushAsync();

                    if (snapshot.IsClosed) return;
                    connection.ResetRead();
                }
            }
            finally
            {
                connection.DecRef();
            }
        };

    // A TCP endpoint that drives the unified client, so tests can observe protocol selection.
    private static async Task DriverHandler(Reactor reactor, TcpConnection connection)
    {
        try
        {
            RingHttpClient upstream = reactor.GetService<RingHttpClient>()!;

            while (true)
            {
                RecvSnapshot snapshot = await connection.ReadAsync();
                string path = Wire.ReadPath(connection, snapshot);

                // A close is not a request. The test client is lock-step (it closes only after
                // reading the response), so a closed snapshot never carries request bytes here -
                // but ReadPath defaults to "/" on an empty snapshot, and dispatching that would
                // send a phantom upstream fetch whose h1 response carries Alt-Svc, promoting the
                // origin to h3 before the test's first real /fetch.
                if (snapshot.IsClosed)
                {
                    return;
                }

                if (path == "/protocol")
                {
                    Wire.Write(connection, 200, upstream.NextProtocol);
                }
                else
                {
                    string detail;
                    int status;
                    try
                    {
                        using HttpClientResponse response = await upstream.GetAsync(path);
                        status = response.Status;
                        detail = Encoding.ASCII.GetString(response.Body.Span).TrimEnd('\n');
                    }
                    catch (Exception e)
                    {
                        status = 599;
                        detail = e.Message;
                    }

                    Wire.Write(connection, 200, $"{status}|{detail}");
                }

                await connection.FlushAsync();
                connection.ResetRead();
            }
        }
        finally
        {
            connection.DecRef();
        }
    }
}
