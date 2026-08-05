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

        runner.Test("ringclient: Alt-Svc: clear retracts a learned h3 endpoint", () =>
        {
            // Nothing listens on the advertised port, so the h3 attempt fails and the request falls
            // back to HTTP/1.1 - which is the only place a retraction is observable at all, since a
            // promoted origin stops producing h1 responses.
            var origin = new SwitchableOrigin("h3=\":9998\"; ma=86400");
            int h1Port = TestServer.Start(origin.Handler);

            int driver = TestServer.Start(DriverHandler, onStart: reactor => RingHttpClient.Start(reactor,
                new RingHttpClientOptions
                {
                    Host = "127.0.0.1",
                    Port = (ushort)h1Port,
                    ServerName = "localhost",
                    AcquireTimeoutMs = 2000,
                    Http3CooldownMs = 500,
                }));

            Client.Get(driver, "/fetch");
            (_, string learned) = Client.Get(driver, "/h3port");
            Assert.Equal("9998", learned);

            // The origin retires h3 before the next attempt.
            origin.Advertise("clear");

            (int status, string body) = Client.Get(driver, "/fetch", timeoutMs: 20_000);
            Assert.Equal(200, status);
            Assert.Equal("200|served by h1", body);

            // Previously the learned port was cached forever, so this stayed 9998 and the client
            // retried a dead endpoint once per cooldown for the rest of its life.
            (_, string afterClear) = Client.Get(driver, "/h3port");
            Assert.Equal("0", afterClear);

            // Past the cooldown, with no endpoint left, it stays on HTTP/1.1.
            Thread.Sleep(800);
            (_, string protocol) = Client.Get(driver, "/protocol");
            Assert.Equal("http/1.1", protocol);
        });

        runner.Test("ringclient: a moved h3 endpoint is followed to its new port", () =>
        {
            (string certPath, string keyPath) = TestCert.Ensure();
            using var engine = new QuicEngine(certPath, keyPath, cidLength: 8, alpn: ["h3"]);

            (_, int realH3Port) = TestServer.StartDatagram(
                onDatagram: null,
                quicFactory: engine.CreateFactory(),
                quicHandle: static (_, connection) => new Nghttp3Connection(connection).RunBufferedAsync(
                    static _ => Nghttp3Response.Text("served by h3")));

            // Advertise a dead port first, so the client learns one and then has to replace it.
            var origin = new SwitchableOrigin("h3=\":9998\"; ma=86400");
            int h1Port = TestServer.Start(origin.Handler);

            int driver = TestServer.Start(DriverHandler, onStart: reactor => RingHttpClient.Start(reactor,
                new RingHttpClientOptions
                {
                    Host = "127.0.0.1",
                    Port = (ushort)h1Port,
                    ServerName = "localhost",
                    AcquireTimeoutMs = 2000,
                    Http3CooldownMs = 500,
                }));

            Client.Get(driver, "/fetch");
            (_, string stale) = Client.Get(driver, "/h3port");
            Assert.Equal("9998", stale);

            origin.Advertise($"h3=\":{realH3Port}\"; ma=86400");

            // Tries the dead port, fails, falls back to h1 - and that h1 response carries the move.
            Client.Get(driver, "/fetch", timeoutMs: 20_000);

            (_, string moved) = Client.Get(driver, "/h3port");
            Assert.Equal(realH3Port.ToString(), moved);

            // Past the cooldown the client uses the new endpoint, and the h3 origin answers.
            Thread.Sleep(800);
            (int status, string body) = Client.Get(driver, "/fetch", timeoutMs: 20_000);
            Assert.Equal(200, status);
            Assert.Equal("200|served by h3", body);
        });

        runner.Test("ringclient: retiring a LIVE h3 pool leaves the client working", () =>
        {
            // Every other retirement test disposes a pool whose only connection had already failed
            // itself, so the interesting case - tearing down a healthy pool with real QUIC state -
            // went unexercised. A short ma= is what makes it reachable: h3 expires, the client
            // drops back to h1, and that h1 response is where a retraction can finally be seen,
            // while the pool underneath is still perfectly alive.
            (string certPath, string keyPath) = TestCert.Ensure();
            using var engine = new QuicEngine(certPath, keyPath, cidLength: 8, alpn: ["h3"]);

            (_, int h3Port) = TestServer.StartDatagram(
                onDatagram: null,
                quicFactory: engine.CreateFactory(),
                quicHandle: static (_, connection) => new Nghttp3Connection(connection).RunBufferedAsync(
                    static _ => Nghttp3Response.Text("served by h3")));

            var origin = new SwitchableOrigin($"h3=\":{h3Port}\"; ma=1");
            int h1Port = TestServer.Start(origin.Handler);

            int driver = TestServer.Start(DriverHandler, onStart: reactor => RingHttpClient.Start(reactor,
                new RingHttpClientOptions
                {
                    Host = "127.0.0.1",
                    Port = (ushort)h1Port,
                    ServerName = "localhost",
                    AcquireTimeoutMs = 3000,
                }));

            Client.Get(driver, "/fetch");                       // learns the endpoint over h1
            (_, string overH3) = Client.Get(driver, "/fetch", timeoutMs: 20_000);
            Assert.Equal("200|served by h3", overH3);           // pool is open and healthy

            // Let the one-second advertisement lapse, then retract it.
            Thread.Sleep(1500);
            origin.Advertise("clear");

            (int status, string body) = Client.Get(driver, "/fetch", timeoutMs: 20_000);
            Assert.Equal(200, status);
            Assert.Equal("200|served by h1", body);

            (_, string port) = Client.Get(driver, "/h3port");
            Assert.Equal("0", port);

            // The client survived disposing a live pool, which is the point.
            (_, string after) = Client.Get(driver, "/fetch", timeoutMs: 20_000);
            Assert.Equal("200|served by h1", after);
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

    // An HTTP/1.1 origin whose Alt-Svc header the test can change between requests, so an origin
    // that moves or retires its h3 endpoint can actually be exercised.
    private sealed class SwitchableOrigin(string altSvc)
    {
        private volatile string _altSvc = altSvc;

        public void Advertise(string value) => _altSvc = value;

        public Func<Reactor, TcpConnection, Task> Handler => async (_, connection) =>
        {
            try
            {
                while (true)
                {
                    RecvSnapshot snapshot = await connection.ReadAsync();
                    Wire.ReadPath(connection, snapshot);

                    const string body = "served by h1";
                    connection.Write(Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 200 OK\r\nAlt-Svc: {_altSvc}\r\n" +
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
                else if (path == "/h3port")
                {
                    Wire.Write(connection, 200, upstream.NegotiatedHttp3Port.ToString());
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
