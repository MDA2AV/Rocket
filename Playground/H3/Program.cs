using System.Text;
using ioxide;
using ioxide.nghttp3;
using ioxide.ngtcp2;
using Playground.Shared;
using Playground.Shared.Http;
using Playground.Shared.Nghttp3;
using Playground.Shared.Quic;

// h3 - HTTP/3 via ioxide.nghttp3, STREAMING dispatch: the handler runs at end-of-headers while the
// body is still arriving, and each chunk read credits the peer's flow-control window - so memory is
// bound by one window, not by the size of the upload.
//
//   /plaintext  a response instance built once and reused: zero allocations per request
//   /upload     streamed request body under flow-control pacing
//   /headers    walks req.Headers.AsSpan() - the KeyValueList, no strings
//   /cookies    req.TryGetCookie + req.Cookies, and sets one back via set-cookie
//   /1k         a fixed 1 KiB body, for load-generator comparability
//   anything    hello, decoding the path only because it goes into text
//
//   curl --http3-only -k https://127.0.0.1:8443/plaintext
//   h2load --alpn-list=h3 -n 1 -c 1 -d bigfile.bin https://127.0.0.1:8443/upload

(QuicEngine engine, QuicOptions quicOptions) = QuicSetup.FromEnvironment("h3");

Nghttp3Options h3Options = Nghttp3Support.OptionsFromEnvironment();
byte[] oneKiB = Responses.BuildOneKiB();
byte[] tcpResponse = Responses.BuildFixedOk(Responses.FixedBodyBytesFromEnvironment());

int exitCode = PlaygroundHost.Run(new PlaygroundSample
{
    Name = "h3",
    Summary = "HTTP/3 via nghttp3, streamed dispatch",
    QuicOptions = quicOptions,
    // :8080 still listens alongside the QUIC port.
    Tcp = (reactor, conn) => ConnectionLoop.ServeAsync(conn, new FixedResponder(tcpResponse)),
    Quic = (reactor, conn) => Nghttp3Support.Track(reactor, new Nghttp3Connection(conn, h3Options))
        .RunStreamingAsync(request => Route(request, oneKiB)),
    OnDrain = Nghttp3Support.ShutdownAll,
});

engine.Dispose();
return exitCode;

static async ValueTask<Nghttp3Response> Route(Nghttp3Request request, byte[] oneKiB)
{
    ReadOnlySpan<byte> path = request.Path.Span;

    // Byte-compare routing: no decode, no allocation, no dictionary.
    if (path.SequenceEqual("/plaintext"u8))
    {
        return Nghttp3Support.PlaintextResponse;
    }

    if (path.SequenceEqual("/upload"u8))
    {
        // The handler runs while the body is still arriving; each chunk read credits the peer's
        // flow-control window, so a slow consumer throttles the sender instead of buffering.
        // Chunks are valid until the next ReadAsync.
        long total = 0;
        while (true)
        {
            ReadOnlyMemory<byte> chunk = await request.BodyReader!.ReadAsync();
            if (chunk.IsEmpty)
            {
                break;
            }
            total += chunk.Length;   // a real app would parse/store the chunk here
        }

        return Nghttp3Response.Text($"received {total} bytes (streamed) over HTTP/3\n");
    }

    if (path.SequenceEqual("/headers"u8))
    {
        // KeyValueList: ordered, duplicate-preserving, enumerated over a span. Names are lowercase
        // on the wire (h3 requires it), values are raw octets.
        var report = new StringBuilder();
        report.Append($"{request.Headers.Count} header field lines\n");
        foreach ((ReadOnlyMemory<byte> name, ReadOnlyMemory<byte> value) in request.Headers.AsSpan())
        {
            report.Append($"  {Encoding.ASCII.GetString(name.Span)}: "
                        + $"{Encoding.ASCII.GetString(value.Span)}\n");
        }
        return Nghttp3Response.Text(report.ToString());
    }

    if (path.SequenceEqual("/cookies"u8))
    {
        // One-shot lookup, byte-level - the common case.
        string session = request.TryGetCookie("session"u8, out ReadOnlyMemory<byte> value)
            ? Encoding.ASCII.GetString(value.Span)
            : "(none)";

        // Enumerating walks every cookie field line: h3 may split one logical cookie header across
        // several, which a plain header lookup would miss.
        int count = 0;
        foreach ((ReadOnlyMemory<byte> _, ReadOnlyMemory<byte> _) in request.Cookies)
        {
            count++;
        }

        var response = new Nghttp3Response
        {
            Body = Encoding.UTF8.GetBytes($"session={session}, {count} cookie(s) sent\n"),
        };
        response.Headers.Add(Nghttp3Support.ContentType, Nghttp3Support.TextPlain);
        response.Headers.Add(Nghttp3Support.SetCookie, Nghttp3Support.SessionCookie);
        return response;
    }

    if (path.SequenceEqual("/1k"u8))
    {
        var response = new Nghttp3Response { Body = oneKiB };
        response.Headers.Add(Nghttp3Support.ContentType, Nghttp3Support.TextPlain);
        return response;
    }

    return Nghttp3Response.Text($"hello {Encoding.ASCII.GetString(path)} over HTTP/3 via io_uring\n");
}
