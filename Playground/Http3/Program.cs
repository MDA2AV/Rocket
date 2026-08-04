using System.Text;
using ioxide;
using ioxide.http3;
using ioxide.ngtcp2;
using Playground.Shared;
using Playground.Shared.Http;
using Playground.Shared.Quic;

// http3 - the PURE C# HTTP/3 stack (ioxide.http3): frames, QPACK (static table + Huffman) and
// request dispatch with zero native h3 code. ngtcp2 still provides the QUIC transport, but nothing
// above it is native - this project deliberately does not reference ioxide.nghttp3, which is the
// whole point of the comparison against Playground/H3.
//
// It rides any QuicConnection via its stream read surface, so it is engine-agnostic.
//
//   curl --http3-only -k https://127.0.0.1:8443/
//   h2load --alpn-list=h3 -n 1 -c 1 -d bigfile.bin https://127.0.0.1:8443/upload

(QuicEngine engine, QuicOptions quicOptions) = QuicSetup.FromEnvironment("http3");

byte[] tcpResponse = Responses.BuildFixedOk(Responses.FixedBodyBytesFromEnvironment());

int exitCode = PlaygroundHost.Run(new PlaygroundSample
{
    Name = "http3",
    Summary = "HTTP/3 via the pure-C# ioxide.http3 stack",
    QuicOptions = quicOptions,
    Tcp = (reactor, conn) => ConnectionLoop.ServeAsync(conn, new FixedResponder(tcpResponse)),
    Quic = (reactor, conn) => new Http3Connection(conn).RunAsync(Route),
    // No nghttp3 session here, so there is nothing to GOAWAY on SIGTERM.
});

engine.Dispose();
return exitCode;

static async ValueTask<Http3Response> Route(Http3Request request)
{
    if (request.Path.Span.SequenceEqual("/upload"u8))
    {
        // Streamed body under flow-control pacing, same as the nghttp3 streamed sample.
        long total = 0;
        while (true)
        {
            ReadOnlyMemory<byte> chunk = await request.BodyReader!.ReadAsync();
            if (chunk.IsEmpty)
            {
                break;
            }
            total += chunk.Length;
        }
        return Http3Response.Text($"received {total} bytes over pure-C# HTTP/3\n");
    }

    return Http3Response.Text(
        $"hello {Encoding.ASCII.GetString(request.Path.Span)} over pure-C# HTTP/3\n");
}
