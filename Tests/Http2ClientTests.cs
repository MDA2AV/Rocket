using System.Text;
using ioxide;
using ioxide.httpclient;
using ioxide.nghttp2;

namespace Ioxide.E2E;

/// <summary>
/// The ring-native HTTP/2 client (h2c - cleartext, prior knowledge) against a real HTTP/2 server.
/// ioxide's own server speaks HTTP/1.1 and HTTP/3 but not HTTP/2, so these run against an nginx
/// sidecar and skip when it is absent:
///
///   docker run -d --name nginx-h2c --network host \
///     -v .../nginx-h2c.conf:/etc/nginx/nginx.conf:ro -v .../doc_root:/doc_root:ro nginx
/// </summary>
internal static class Http2ClientTests
{
    private const string SidecarHost = "127.0.0.1";
    private const ushort SidecarPort = 14464;

    public static void Register(Runner runner)
    {
        bool noSidecar = !Sidecars.Reachable(SidecarHost, SidecarPort);

        runner.Test("httpclient2: GET over h2c", () =>
        {
            int driver = TestServer.Start(DriverHandler, onStart: reactor =>
                Http2ClientPool.Start(reactor, Options()));

            (int status, string body) = Client.Get(driver, "/index.html");
            Assert.Equal(200, status);
            Assert.Equal("200|1024", body);   // the sidecar's 1 KiB object
        }, skip: noSidecar);

        runner.Test("httpclient2: many requests multiplex over one connection", () =>
        {
            int driver = TestServer.Start(DriverHandler, onStart: reactor =>
                Http2ClientPool.Start(reactor, Options()));

            for (int i = 0; i < 25; i++)
            {
                (int status, string body) = Client.Get(driver, "/index.html");
                Assert.Equal(200, status);
                Assert.Equal("200|1024", body);
            }

            // All 25 rode a single HTTP/2 connection - that is the point of multiplexing.
            (_, string stats) = Client.Get(driver, "/connstats");
            Assert.Equal("conns=1", stats);
        }, skip: noSidecar);
    }

    private static Http2ClientOptions Options() => new()
    {
        Host = SidecarHost,
        Port = SidecarPort,
        PoolSize = 1,
    };

    private static async Task DriverHandler(Reactor reactor, TcpConnection connection)
    {
        try
        {
            Http2ClientPool upstream = reactor.GetService<Http2ClientPool>()!;

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
                        detail = response.Body.Length.ToString();   // size, not the 1 KiB of 'x'
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
