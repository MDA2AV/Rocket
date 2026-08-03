using System.Text;
using ioxide;
using ioxide.httpclient;
using ioxide.httpclient3;
using ioxide.nghttp3;
using ioxide.ngtcp2;

namespace Ioxide.E2E;

/// <summary>
/// The unified client: one request/response type over either protocol, with the protocol chosen per
/// origin. Negotiation is Alt-Svc driven, because h1 (TCP) and h3 (QUIC) share no handshake to
/// negotiate within - the origin advertises h3 on an HTTP/1.1 response, and later requests use it.
/// </summary>
internal static class RingHttpClientTests
{
    public static void Register(Runner runner)
    {
        // The parser first: Alt-Svc is the whole negotiation signal, and it arrives as attacker-
        // adjacent bytes from the network.
        runner.Test("ringclient: Alt-Svc parsing picks h3 on the same origin only", () =>
        {
            Assert.True(RingHttpClient.TryParseH3Port("h3=\":8443\""u8, out ushort p1) && p1 == 8443, "plain h3");
            Assert.True(RingHttpClient.TryParseH3Port("h3=\":8443\"; ma=86400"u8, out ushort p2) && p2 == 8443, "h3 with ma");

            // Drafts we don't speak come first; the real h3 entry is later in the list.
            Assert.True(RingHttpClient.TryParseH3Port("h3-29=\":1111\", h3=\":8443\"; ma=3600"u8, out ushort p3)
                        && p3 == 8443, "h3 after a draft entry");

            // An alternative HOST is a different origin - not ours to connect to.
            Assert.True(!RingHttpClient.TryParseH3Port("h3=\"other.example:8443\""u8, out _), "alternative host ignored");

            // Retraction, junk, and a nonsense port must all leave us on h1.
            Assert.True(!RingHttpClient.TryParseH3Port("clear"u8, out _), "clear retracts");
            Assert.True(!RingHttpClient.TryParseH3Port("h2=\":443\""u8, out _), "h2 is not h3");
            Assert.True(!RingHttpClient.TryParseH3Port("h3=\":0\""u8, out _), "port 0 rejected");
            Assert.True(!RingHttpClient.TryParseH3Port("garbage"u8, out _), "junk rejected");
        });

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
            int h1Port = TestServer.Start(AdvertisingOriginHandler(9999));

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
            int h1Port = TestServer.Start(AdvertisingOriginHandler(9999));

            // Http3Port is pinned at a port nothing listens on, so the h3 attempt cannot succeed.
            int driver = TestServer.Start(DriverHandler, onStart: reactor => RingHttpClient.Start(reactor,
                new RingHttpClientOptions
                {
                    Host = "127.0.0.1",
                    Port = (ushort)h1Port,
                    Http3Port = 9999,
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

                if (snapshot.IsClosed) return;
                connection.ResetRead();
            }
        }
        finally
        {
            connection.DecRef();
        }
    }
}
