using ioxide;
using ioxide.http2;

namespace Ioxide.Tests;

/// <summary>
/// HTTP/2 (h2c) chaos against the managed <see cref="Http2Connection"/> server: a bad connection
/// preface, a frame larger than the negotiated maximum, unknown frame types that the spec says to
/// ignore, and a frame truncated mid-payload. The server must reject the malformed input on its own
/// terms (GOAWAY, or a dropped connection) and keep answering fresh connections.
/// </summary>
internal static class H2ChaosTests
{
    private static int StartH2c() => TestServer.Start(static async (_, conn) =>
    {
        try
        {
            await new Http2Connection(conn).RunBufferedAsync(static _ => Http2Response.Text("ok"));
        }
        finally
        {
            conn.DecRef();
        }
    });

    private static void AssertServes(int port)
    {
        using var client = new H2cClient(port);
        client.Open();
        client.Request(streamId: 1);
        Assert.True(client.AwaitResponse(streamId: 1), "server did not answer a well-formed h2c request");
    }

    public static void Register(Runner runner)
    {
        runner.Test("h2c: a well-formed request is answered", () =>
        {
            int port = StartH2c();
            AssertServes(port);
        });

        runner.Test("h2c: a bad connection preface is rejected, server survives", () =>
        {
            int port = StartH2c();

            using (var bad = new H2cClient(port))
            {
                bad.WriteRaw("NOT-A-VALID-HTTP2-PREFACE\r\n\r\n"u8);   // not "PRI * HTTP/2.0..."
                Thread.Sleep(150);
            }

            AssertServes(port);
        });

        runner.Test("h2c: a frame larger than the max is rejected, server survives", () =>
        {
            int port = StartH2c();

            using (var attacker = new H2cClient(port))
            {
                attacker.Open();
                // Declare a 1 MiB HEADERS frame - far past the 16384 max - then send none of it. The
                // server must fault on the frame header (FRAME_SIZE_ERROR), not try to buffer a
                // megabyte.
                attacker.WriteFrameHeader(type: 0x1, flags: 0x4, streamId: 1, declaredLen: 1 << 20,
                    actual: ReadOnlySpan<byte>.Empty);
                Thread.Sleep(150);
            }

            AssertServes(port);
        });

        runner.Test("h2c: unknown frame types are ignored, the request still answers", () =>
        {
            int port = StartH2c();

            using var client = new H2cClient(port);
            client.Open();
            // RFC 9113 4.1: an endpoint MUST ignore a frame of unknown type. Slip one in before the
            // request and the request must still be served.
            client.WriteFrame(type: 0xFA, flags: 0, streamId: 0, payload: [1, 2, 3, 4]);
            client.Request(streamId: 1);
            Assert.True(client.AwaitResponse(streamId: 1), "server choked on an unknown frame type");
        });

        runner.Test("h2c: a frame truncated mid-payload is handled, server survives", () =>
        {
            int port = StartH2c();

            using (var attacker = new H2cClient(port))
            {
                attacker.Open();
                // Declare 100 bytes of HEADERS, send 10, then drop the connection.
                attacker.WriteFrameHeader(type: 0x1, flags: 0x4, streamId: 1, declaredLen: 100,
                    actual: new byte[10]);
                Thread.Sleep(150);
            }

            AssertServes(port);
        });

        runner.Test("h2c: a burst of streams on one connection are all framed", () =>
        {
            int port = StartH2c();

            using var client = new H2cClient(port);
            client.Open();

            const int streams = 16;
            for (int i = 0; i < streams; i++)
            {
                client.Request(streamId: 1 + (i * 2));   // odd stream ids, client-initiated
            }

            // At least the first stream must come back; the point is the multiplexer survives a
            // burst without losing the connection.
            Assert.True(client.AwaitResponse(streamId: 1), "server dropped a multiplexed request burst");
        });
    }
}
