using System.Text;
using ioxide;

namespace Playground.Shared.Http;

/// <summary>
/// Canned responses and the byte-level writers the TCP samples share. Nothing here allocates per
/// request: the fixed bodies are built once at startup and the framing is formatted into stack
/// buffers.
/// </summary>
public static class Responses
{
    public static ReadOnlySpan<byte> NotFound =>
        "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\n\r\n"u8;

    public static ReadOnlySpan<byte> ServerError =>
        "HTTP/1.1 500 Internal Server Error\r\nContent-Length: 0\r\n\r\n"u8;

    public static ReadOnlySpan<byte> JsonHeader =>
        "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 13\r\n\r\n"u8;

    /// <summary>
    /// A complete, pre-encoded plaintext response with an <paramref name="bodyBytes"/>-byte body.
    /// Built once; the handler writes the same buffer for every request.
    /// </summary>
    public static byte[] BuildFixedOk(int bodyBytes)
    {
        byte[] body = bodyBytes == 2
            ? "ok"u8.ToArray()
            : [.. Enumerable.Repeat((byte)'x', bodyBytes - 1), (byte)'\n'];

        return
        [
            .. Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {body.Length}\r\n\r\n"),
            .. body,
        ];
    }

    /// <summary>
    /// The body size <c>PLAYGROUND_BODY</c> asks for, falling back to 2 ("ok") when unset or
    /// non-positive. 1024 matches the object size load-generator grids conventionally measure.
    /// </summary>
    public static int FixedBodyBytesFromEnvironment()
        => Env.Int("PLAYGROUND_BODY", 2) is var body && body > 0 ? body : 2;

    /// <summary>A fixed 1 KiB body - the object size load-generator grids conventionally measure.</summary>
    public static byte[] BuildOneKiB()
    {
        var body = new byte[1024];
        body.AsSpan().Fill((byte)'x');
        body[^1] = (byte)'\n';
        return body;
    }

    // Copy native memory through the write slab in chunks and flush - one flush for small payloads,
    // a short sequence for ones bigger than the slab.
    private const int BodyChunk = 12 * 1024;

    public static async Task SendChunkedAsync(TcpConnection conn, nint data, int length)
    {
        int sent = 0;
        while (true)
        {
            int chunk = Math.Min(length - sent, BodyChunk);
            WriteBodyChunk(conn, data + sent, chunk);
            await conn.FlushAsync();
            sent += chunk;

            if (sent >= length) return;
        }
    }

    private static unsafe void WriteBodyChunk(TcpConnection conn, nint chunk, int length)
    {
        conn.Write(new ReadOnlySpan<byte>((void*)chunk, length));
    }

    /// <summary>Copy <paramref name="source"/> into <paramref name="destination"/>, returning its length.</summary>
    public static int Copy(Span<byte> destination, ReadOnlySpan<byte> source)
    {
        source.CopyTo(destination);
        return source.Length;
    }
}
