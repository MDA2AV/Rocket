using System.Text;
using ioxide;
using ioxide.nghttp3;
using ioxide.ngtcp2;
using Playground.Shared;
using Playground.Shared.Http;
using Playground.Shared.Nghttp3;
using Playground.Shared.Quic;

// h3-buffered - HTTP/3 via ioxide.nghttp3, BUFFERED dispatch: the handler waits for end-of-stream,
// so the WHOLE body is already in request.Body - no BodyReader, no pacing - while the handler may
// still await (a PgPool query, Redis, any ioxide-native awaitable resumes inline on the reactor).
//
// The trade against the streamed sample: memory holds the entire body, so this suits normal-sized
// requests. Use Playground/H3 when uploads can be large or hostile.
//
//   curl --http3-only -k https://127.0.0.1:8443/plaintext

(QuicEngine engine, QuicOptions quicOptions) = QuicSetup.FromEnvironment("h3-buffered");

Nghttp3Options h3Options = Nghttp3Support.OptionsFromEnvironment();
byte[] tcpResponse = Responses.BuildFixedOk(Responses.FixedBodyBytesFromEnvironment());

int exitCode = PlaygroundHost.Run(new PlaygroundSample
{
    Name = "h3-buffered",
    Summary = "HTTP/3 via nghttp3, buffered dispatch (whole body in req.Body)",
    QuicOptions = quicOptions,
    Tcp = (reactor, conn) => ConnectionLoop.ServeAsync(conn, new FixedResponder(tcpResponse)),
    Quic = (reactor, conn) => Nghttp3Support.Track(reactor, new Nghttp3Connection(conn, h3Options))
        .RunBufferedAsync(Route),
    OnDrain = Nghttp3Support.ShutdownAll,
});

engine.Dispose();
return exitCode;

static async ValueTask<Nghttp3Response> Route(Nghttp3Request request)
{
    ReadOnlySpan<byte> path = request.Path.Span;

    if (path.SequenceEqual("/plaintext"u8))
    {
        return Nghttp3Support.PlaintextResponse;
    }

    if (path.SequenceEqual("/upload"u8))
    {
        // Complete before we run: Length is a property read, and the bytes are all here. This is
        // where a real await (storing request.Body, say) would slot in.
        await ValueTask.CompletedTask;
        return Nghttp3Response.Text($"received {request.Body.Length} bytes (buffered) over HTTP/3\n");
    }

    return Nghttp3Response.Text($"hello {Encoding.ASCII.GetString(path)} over HTTP/3 via io_uring\n");
}
