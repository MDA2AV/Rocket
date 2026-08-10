using System.Text;
using ioxide;
using ioxide.httpclient;

namespace Ioxide.Tests;

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

    // A second sidecar serving the same shape of object WITH trailers (nginx add_trailer), because
    // a trailer section is an extra HEADERS frame the plain sidecar never emits.
    //
    //   docker run -d --name nginx-trailer --network host \
    //     -v .../nginx-trailer.conf:/etc/nginx/nginx.conf:ro -v .../trailer_root:/doc_root:ro nginx
    private const ushort TrailerSidecarPort = 14466;

    public static void Register(Runner runner)
    {
        bool noSidecar = !Sidecars.Reachable(SidecarHost, SidecarPort);
        bool noTrailerSidecar = !Sidecars.Reachable(SidecarHost, TrailerSidecarPort);

        runner.Test("httpclient h2: GET over h2c", () =>
        {
            int driver = TestServer.Start(DriverHandler, onStart: reactor =>
                Http2ClientPool.Start(reactor, Options()));

            (int status, string body) = Client.Get(driver, "/index.html");
            Assert.Equal(200, status);
            Assert.Equal("200|1024", body);   // the sidecar's 1 KiB object
        }, skip: noSidecar);

        // Regression guard: request bodies were once silently dropped - accounted for as sent
        // without a DATA frame ever leaving. Every other test here is a GET, so nothing else on
        // this connection would notice.
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

        runner.Test("httpclient h2: MaxResponseBytes is enforced, not merely configured", () =>
        {
            // The option was plumbed all the way to the connection and then used only in the
            // exception TEXT - nothing ever applied it to the response arena, so h2 silently kept
            // whatever the shared default happened to be and the error message named a limit it
            // was not enforcing. The sidecar's 1 KiB object against a 16-byte ceiling settles it.
            int driver = TestServer.Start(DriverHandler, onStart: reactor =>
                Http2ClientPool.Start(reactor, Options() with { MaxResponseBytes = 16 }));

            (int status, string body) = Client.Get(driver, "/index.html", timeoutMs: 20_000);
            Assert.Equal(200, status);
            Assert.True(body.StartsWith("599|"), $"expected the request to fail, got: {body}");
            Assert.True(body.Contains("MaxResponseBytes"), $"should name the limit, got: {body}");
        }, skip: noSidecar);

        runner.Test("httpclient h2: a trailered response survives and keeps its body", () =>
        {
            // Trailers are a SECOND field section on a stream that already has one, which is the
            // same shape as the real response arriving after a 1xx. While the second section
            // replaced the response, the assembled one was discarded with BodyStart/BodyLength
            // still describing its arena, and end-of-stream then sliced those offsets out of the
            // fresh, near-empty one: a large body threw ArgumentOutOfRangeException and killed the
            // process, a small one came back as silent garbage with status 0.
            //
            // The 20 KB object is deliberate. It cannot fit the trailer section's arena, so a
            // regression here is a dead process rather than a merely wrong-looking string.
            int driver = TestServer.Start(DriverHandler, onStart: reactor =>
                Http2ClientPool.Start(reactor, TrailerOptions()));

            (int status, string body) = Client.Get(driver, "/big.html", timeoutMs: 20_000);
            Assert.Equal(200, status);
            Assert.Equal("200|20000", body);

            // Still serving afterwards - the half that a crash would take with it.
            (_, string second) = Client.Get(driver, "/big.html", timeoutMs: 20_000);
            Assert.Equal("200|20000", second);
        }, skip: noTrailerSidecar);

        // The two paths below need an origin that reports back what it RECEIVED, which nginx has no
        // way to do - so they run against ioxide's own HTTP/2 server. Both exercise client code the
        // sidecar tests never reach, because a 1 KiB GET fits in one window and one frame.

        runner.Test("httpclient h2: a body past the flow-control window arrives whole", () =>
        {
            // 1 MiB against a 65535-byte connection window: the body cannot go out in one pass, so
            // the client has to send what it has credit for, park, and resume on each WINDOW_UPDATE
            // the origin sends back. Getting this wrong either truncates the body or blows the
            // window and earns a FLOW_CONTROL_ERROR - the origin echoes the length, so both show.
            const int BodyBytes = 1024 * 1024;

            int origin = StartEchoOrigin();
            int driver = TestServer.Start(PostSizeDriver(BodyBytes), onStart: reactor =>
                Http2ClientPool.Start(reactor, OriginOptions(origin)));

            (int status, string body) = Client.Get(driver, "/echo", timeoutMs: 30_000);
            Assert.Equal(200, status);
            Assert.Equal($"200|{BodyBytes}", body);
        });

        runner.Test("h2 server: a streamed request body is read as it arrives", () =>
        {
            // Same 1 MiB upload, but the origin never holds it: StreamRequestBodies dispatches at
            // the headers and hands the handler a reader. Window credit goes back only as chunks
            // are read, so if that crediting were wrong the upload would stall at the first window
            // and this would time out rather than come back short.
            const int BodyBytes = 1024 * 1024;

            int origin = StartStreamedOrigin();
            int driver = TestServer.Start(PostSizeDriver(BodyBytes), onStart: reactor =>
                Http2ClientPool.Start(reactor, OriginOptions(origin)));

            (int status, string body) = Client.Get(driver, "/echo", timeoutMs: 30_000);
            Assert.Equal(200, status);
            Assert.Equal($"200|{BodyBytes}", body);
        });

        runner.Test("httpclient h2: a header block past the frame size continues", () =>
        {
            // 40 headers of ~512 bytes overflows the 16 KiB maximum frame size, so the field
            // section has to leave as HEADERS + CONTINUATION. The block cannot be split anywhere
            // else on the connection either - HPACK is one stream, and a decoder needs the pieces
            // contiguous - so a mistake here desynchronises the table rather than failing cleanly.
            int origin = StartEchoOrigin();
            int driver = TestServer.Start(ManyHeadersDriver(count: 40, valueBytes: 512), onStart: reactor =>
                Http2ClientPool.Start(reactor, OriginOptions(origin)));

            (int status, string body) = Client.Get(driver, "/headers", timeoutMs: 30_000);
            Assert.Equal(200, status);
            Assert.Equal("200|40", body);
        });
    }

    private static Http2ClientOptions OriginOptions(int port) => new()
    {
        Host = "127.0.0.1",
        Port = (ushort)port,
        PoolSize = 1,
    };

    /// <summary>
    /// An ioxide HTTP/2 origin that answers with what it received: the body length for a request
    /// that carried one, otherwise the number of ordinary header fields.
    /// </summary>
    private static int StartEchoOrigin() => TestServer.Start(async (_, connection) =>
    {
        try
        {
            await new ioxide.http2.Http2Connection(connection).RunBufferedAsync(request =>
                new ioxide.http2.Http2Response
                {
                    Status = 200,
                    Body = Encoding.ASCII.GetBytes(
                        (request.Body.Length > 0 ? request.Body.Length : request.Headers.Count).ToString()),
                });
        }
        finally
        {
            connection.DecRef();
        }
    });

    /// <summary>
    /// The same origin with the body STREAMED: it counts the bytes it is handed and never keeps
    /// them, so the answer proves the whole body arrived without any of it being held.
    /// </summary>
    private static int StartStreamedOrigin() => TestServer.Start(async (_, connection) =>
    {
        try
        {
            var options = new ioxide.http2.Http2Options { StreamRequestBodies = true };
            await new ioxide.http2.Http2Connection(connection, options).RunBufferedAsync(async request =>
            {
                int total = 0;
                if (request.BodyReader is { } reader)
                {
                    while (true)
                    {
                        ReadOnlyMemory<byte> chunk = await reader.ReadAsync();
                        if (chunk.IsEmpty)
                        {
                            break;
                        }
                        total += chunk.Length;
                    }
                }

                return new ioxide.http2.Http2Response
                {
                    Status = 200,
                    Body = Encoding.ASCII.GetBytes(total.ToString()),
                };
            });
        }
        finally
        {
            connection.DecRef();
        }
    });

    private static Func<Reactor, TcpConnection, Task> PostSizeDriver(int bodyBytes)
        => (reactor, connection) => DriveOnce(reactor, connection, upstream =>
        {
            byte[] payload = new byte[bodyBytes];
            payload.AsSpan().Fill((byte)'z');
            return upstream.PostAsync("/echo"u8.ToArray(), payload);
        });

    private static Func<Reactor, TcpConnection, Task> ManyHeadersDriver(int count, int valueBytes)
        => (reactor, connection) => DriveOnce(reactor, connection, upstream =>
        {
            var request = new HttpClientRequest(HttpMethods.Get, "/headers");
            for (int i = 0; i < count; i++)
            {
                request.Headers.Add(
                    Encoding.ASCII.GetBytes($"x-filler-{i:D3}"),
                    Encoding.ASCII.GetBytes(new string('v', valueBytes)));
            }
            return upstream.SendAsync(request);
        });

    // One request per inbound connection, answering with "status|body" so the assertion reads the
    // origin's own account of what arrived.
    private static async Task DriveOnce(Reactor reactor, TcpConnection connection,
        Func<Http2ClientPool, ValueTask<HttpClientResponse>> exchange)
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
                Wire.ReadPath(connection, snapshot);

                string detail;
                int status;
                try
                {
                    using HttpClientResponse response = await exchange(upstream);
                    status = response.Status;
                    detail = Encoding.ASCII.GetString(response.Body.Span);
                }
                catch (Exception e)
                {
                    status = 599;
                    detail = e.Message;
                }

                Wire.Write(connection, 200, $"{status}|{detail}");
                await connection.FlushAsync();
                connection.ResetRead();
            }
        }
        finally
        {
            connection.DecRef();
        }
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

    private static Http2ClientOptions TrailerOptions() => Options() with { Port = TrailerSidecarPort };

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
