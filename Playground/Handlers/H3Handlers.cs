using System.Text;
using ioxide;
using ioxide.http3;
using ioxide.nghttp3;
using Playground.Http;

namespace Playground.Handlers;

/// <summary>
/// The HTTP/3 modes. Two flavors ride ioxide.nghttp3 (streamed and buffered dispatch); the third
/// rides the pure-C# ioxide.http3 stack. Only the nghttp3 ones hold connections that need a GOAWAY
/// on shutdown - see <see cref="ShutdownAll"/>.
/// </summary>
internal static class H3Handlers
{
    // Field names/values reused across responses: static byte literals, so a response that uses
    // only these allocates nothing beyond the response object itself.
    private static readonly byte[] ContentType   = "content-type"u8.ToArray();
    private static readonly byte[] TextPlain     = "text/plain"u8.ToArray();
    private static readonly byte[] SetCookie     = "set-cookie"u8.ToArray();
    private static readonly byte[] ServerName    = "server"u8.ToArray();
    private static readonly byte[] ServerValue   = "ioxide"u8.ToArray();
    private static readonly byte[] SessionCookie = "session=demo; Path=/; HttpOnly; SameSite=Lax"u8.ToArray();

    private static readonly byte[] OneKiBBody = Responses.BuildOneKiB();

    /// <summary>
    /// The allocation-free response pattern: build it ONCE and reuse the instance for every
    /// request. Legal because the h3 layer copies status, headers and body into nghttp3
    /// synchronously at submit and never retains the object - so a static response costs zero
    /// allocations per request, unlike <c>Nghttp3Response.Text($"...")</c> which encodes a fresh
    /// string every time. This is what a hot path should look like.
    /// </summary>
    private static readonly Nghttp3Response PlaintextResponse = BuildPlaintextResponse();

    private static Nghttp3Response BuildPlaintextResponse()
    {
        var response = new Nghttp3Response { Body = "Hello, World!"u8.ToArray() };
        response.Headers.Add(ContentType, TextPlain);
        response.Headers.Add(ServerName, ServerValue);
        return response;
    }

    private static Nghttp3Options BuildOptions(long qpackCapacity) => new()
    {
        QpackDynamicTableCapacity = qpackCapacity,
        QpackBlockedStreams = qpackCapacity > 0 ? 100 : 0,
    };

    // Live nghttp3 connections, so a SIGTERM can GOAWAY them all. Each reactor only ever adds its
    // own, but a plain lock keeps the signal handler - which runs off-reactor - honest.
    private static readonly List<(Reactor Reactor, Nghttp3Connection Connection)> Live = [];

    private static Nghttp3Connection Track(Reactor reactor, Nghttp3Connection connection)
    {
        lock (Live)
        {
            Live.Add((reactor, connection));
        }
        return connection;
    }

    /// <summary>
    /// Graceful drain: GOAWAY every live nghttp3 connection (called from the SIGTERM handler, i.e.
    /// OFF the reactor threads - so each Shutdown is marshalled onto its owning reactor, which is
    /// where nghttp3 and the send path must be touched). Each connection finishes its in-flight
    /// requests, then closes itself.
    /// </summary>
    public static void ShutdownAll()
    {
        lock (Live)
        {
            foreach ((Reactor reactor, Nghttp3Connection connection) in Live)
            {
                reactor.ScheduleOnReactor(static state => ((Nghttp3Connection)state!).Shutdown(), connection);
            }
            Live.Clear();
        }
    }

    /// <summary>
    /// nghttp3, STREAMING flavor - dispatch at end-of-headers, bodies pulled under flow-control
    /// pacing. Routes, each demonstrating one part of the byte-level surface:
    ///
    ///   /plaintext  static response, zero allocations per request
    ///   /upload     streamed request body (memory bound = one window, not the body size)
    ///   /headers    walks req.Headers.AsSpan() - the KeyValueList, no strings
    ///   /cookies    req.TryGetCookie + req.Cookies, and sets one back via set-cookie
    ///   /1k         a fixed 1 KiB body, for load-generator comparability
    ///   anything    hello, decoding the path only because it goes into text
    ///
    ///   h2load --alpn-list=h3 -n 1 -c 1 -d bigfile.bin https://127.0.0.1:8443/upload
    /// </summary>
    public static Task Streamed(Reactor reactor, QuicConnection conn, long qpackCapacity)
        => Track(reactor, new Nghttp3Connection(conn, BuildOptions(qpackCapacity)))
            .RunStreamingAsync(static async request =>
            {
                ReadOnlySpan<byte> path = request.Path.Span;

                // Byte-compare routing: no decode, no allocation, no dictionary.
                if (path.SequenceEqual("/plaintext"u8))
                {
                    return PlaintextResponse;
                }

                if (path.SequenceEqual("/upload"u8))
                {
                    // The handler runs while the body is still arriving; each chunk read credits
                    // the peer's flow-control window, so a slow consumer throttles the sender
                    // instead of buffering. Chunks are valid until the next ReadAsync.
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
                    // KeyValueList: ordered, duplicate-preserving, enumerated over a span. Names are
                    // lowercase on the wire (h3 requires it), values are raw octets.
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

                    // Enumerating walks every cookie field line: h3 may split one logical cookie
                    // header across several, which a plain header lookup would miss.
                    int count = 0;
                    foreach ((ReadOnlyMemory<byte> _, ReadOnlyMemory<byte> _) in request.Cookies)
                    {
                        count++;
                    }

                    var response = new Nghttp3Response
                    {
                        Body = Encoding.UTF8.GetBytes($"session={session}, {count} cookie(s) sent\n"),
                    };
                    response.Headers.Add(ContentType, TextPlain);
                    response.Headers.Add(SetCookie, SessionCookie);   // repeat the Add for more cookies
                    return response;
                }

                if (path.SequenceEqual("/1k"u8))
                {
                    var oneKiB = new Nghttp3Response { Body = OneKiBBody };
                    oneKiB.Headers.Add(ContentType, TextPlain);
                    return oneKiB;
                }

                return Nghttp3Response.Text($"hello {Encoding.ASCII.GetString(path)} over HTTP/3 via io_uring\n");
            });

    /// <summary>
    /// nghttp3, BUFFERED-ASYNC flavor: dispatch waits for end-of-stream, so the WHOLE body is
    /// already in <c>request.Body</c> - no BodyReader, no pacing - while the handler may still await
    /// (a PgPool query, Redis, any ioxide-native awaitable resumes inline on the reactor). The
    /// trade: memory holds the entire body, so it suits normal-sized requests; use the streamed
    /// flavor when uploads can be large or hostile.
    /// </summary>
    public static Task Buffered(Reactor reactor, QuicConnection conn, long qpackCapacity)
        => Track(reactor, new Nghttp3Connection(conn, BuildOptions(qpackCapacity)))
            .RunBufferedAsync(static async request =>
            {
                ReadOnlySpan<byte> path = request.Path.Span;

                if (path.SequenceEqual("/plaintext"u8))
                {
                    return PlaintextResponse;
                }

                if (path.SequenceEqual("/upload"u8))
                {
                    // Complete before we run: Length is a property read, and the bytes are all here.
                    // This is where a real await (storing request.Body, say) would slot in.
                    await ValueTask.CompletedTask;
                    return Nghttp3Response.Text($"received {request.Body.Length} bytes (buffered) over HTTP/3\n");
                }

                return Nghttp3Response.Text($"hello {Encoding.ASCII.GetString(path)} over HTTP/3 via io_uring\n");
            });

    /// <summary>
    /// The pure-C# HTTP/3 stack (ioxide.http3: frames + QPACK + Huffman, no native h3 code) on the
    /// same streaming surface as <see cref="Streamed"/> - POST /upload pulls the body chunk by chunk
    /// under flow-control pacing, everything else answers hello. Not tracked for GOAWAY: it holds
    /// no nghttp3 session.
    /// </summary>
    public static Task PureCSharp(Reactor reactor, QuicConnection conn)
        => new Http3Connection(conn).RunAsync(static async req =>
        {
            if (req.Path.Span.SequenceEqual("/upload"u8))
            {
                long total = 0;
                while (true)
                {
                    ReadOnlyMemory<byte> chunk = await req.BodyReader!.ReadAsync();
                    if (chunk.IsEmpty)
                    {
                        break;
                    }
                    total += chunk.Length;
                }
                return Http3Response.Text($"received {total} bytes over pure-C# HTTP/3\n");
            }

            return Http3Response.Text($"hello {Encoding.ASCII.GetString(req.Path.Span)} over pure-C# HTTP/3\n");
        });
}
