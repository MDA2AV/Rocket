using System.Text;
using ioxide;
using ioxide.httpclient;

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

        runner.Test("httpclient h2: GET over h2c", () =>
        {
            int driver = TestServer.Start(DriverHandler, onStart: reactor =>
                Http2ClientPool.Start(reactor, Options()));

            (int status, string body) = Client.Get(driver, "/index.html");
            Assert.Equal(200, status);
            Assert.Equal("200|1024", body);   // the sidecar's 1 KiB object
        }, skip: noSidecar);

        // Regression guard: request bodies were silently dropped by the shim (NGHTTP2_DATA_FLAG_NO_COPY
        // with a no-op send_data callback made nghttp2 account each DATA frame as sent without ever
        // emitting it). Every earlier test was a GET, so nothing caught it.
        runner.Test("httpclient h2: POST body actually reaches the origin", () =>
        {
            int driver = TestServer.Start(PostDriverHandler, onStart: reactor =>
                Http2ClientPool.Start(reactor, Options()));

            // nginx answers a POST to a static file with 405, which is proof enough that the
            // request arrived intact - a dropped body would hang the stream until timeout instead.
            (int status, string body) = Client.Get(driver, "/index.html");
            Assert.Equal(200, status);
            Assert.Equal("405", body);
        }, skip: noSidecar);

        runner.Test("httpclient h2: many requests multiplex over one connection", () =>
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

    // Sends a POST with a 4 KiB body and reports the status, so a dropped body shows up as a
    // hang/timeout rather than a pass.
    private static async Task PostDriverHandler(Reactor reactor, TcpConnection connection)
    {
        try
        {
            Http2ClientPool upstream = reactor.GetService<Http2ClientPool>()!;

            while (true)
            {
                RecvSnapshot snapshot = await connection.ReadAsync();
                if (snapshot.IsClosed)
                {
                    return;
                }
                string path = Wire.ReadPath(connection, snapshot);

                string detail;
                try
                {
                    byte[] payload = new byte[4096];
                    payload.AsSpan().Fill((byte)'z');
                    using HttpClientResponse response = await upstream.PostAsync(
                        System.Text.Encoding.ASCII.GetBytes(path), payload);
                    detail = response.Status.ToString();
                }
                catch (Exception e)
                {
                    detail = e.Message;
                }

                Wire.Write(connection, 200, detail);
                await connection.FlushAsync();
                connection.ResetRead();
            }
        }
        finally
        {
            connection.DecRef();
        }
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
