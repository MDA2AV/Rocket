using System.Buffers;
using System.Text;

namespace Examples;

/// <summary>The application's request type - the landing page's "YOUR code".</summary>
public readonly record struct Request(string Path);

/// <summary>
/// The hand-rolled HTTP bits every landing-page example delegates to: enough parsing to frame a
/// request (terminated by \r\n\r\n) and pull the target path out of the request line.
/// </summary>
public static class Http
{
    private static ReadOnlySpan<byte> HeaderEnd => "\r\n\r\n"u8;

    /// <summary>
    /// Pipes flavor: walk one complete request off the front of <paramref name="buffer"/>,
    /// slicing it forward past the request. False when the request is still partial.
    /// </summary>
    public static bool TryParseRequest(ref ReadOnlySequence<byte> buffer, out Request request)
    {
        var reader = new SequenceReader<byte>(buffer);

        if (!reader.TryReadTo(out ReadOnlySequence<byte> header, HeaderEnd))
        {
            request = default;
            return false;
        }

        request = new Request(ExtractPath(header));
        buffer = buffer.Slice(reader.Position);
        return true;
    }

    /// <summary>
    /// Raw flavor: parse one complete request at the front of <paramref name="data"/> and report
    /// how many bytes it occupied. False when the request is still partial.
    /// </summary>
    public static bool TryParseRequest(ReadOnlySequence<byte> data, out Request request, out long length)
    {
        var reader = new SequenceReader<byte>(data);

        if (!reader.TryReadTo(out ReadOnlySequence<byte> header, HeaderEnd))
        {
            request = default;
            length = 0;
            return false;
        }

        request = new Request(ExtractPath(header));
        length = reader.Consumed;
        return true;
    }

    /// <summary>The raw category's fixed plaintext response.</summary>
    public static ReadOnlySpan<byte> OkResponse =>
        "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 2\r\n\r\nok"u8;

    /// <summary>Route however you like - here, a path-to-query map.</summary>
    public static string SqlFor(string path) => path switch
    {
        "/sleep" => "SELECT 42 FROM pg_sleep(0.1)",
        _        => "SELECT 42",
    };

    // "GET /path HTTP/1.1" -> "/path" (query string stripped).
    private static string ExtractPath(in ReadOnlySequence<byte> header)
    {
        ReadOnlySpan<byte> line = header.IsSingleSegment
            ? header.FirstSpan
            : header.ToArray();   // fragmented header - rare, and headers are small

        int firstSpace = line.IndexOf((byte)' ');
        if (firstSpace < 0) return "/";

        ReadOnlySpan<byte> afterMethod = line[(firstSpace + 1)..];
        int secondSpace = afterMethod.IndexOf((byte)' ');
        ReadOnlySpan<byte> target = secondSpace > 0 ? afterMethod[..secondSpace] : afterMethod;

        int query = target.IndexOf((byte)'?');
        if (query >= 0) target = target[..query];

        return Encoding.ASCII.GetString(target);
    }
}
