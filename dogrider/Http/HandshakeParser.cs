using System.Buffers;
using System.Buffers.Text;
using System.Text;

namespace dogrider.Http;

/// <summary>
/// Minimal HTTP/1.1 parser for the WebSocket upgrade request only. Not a general HTTP parser.
/// Looks for: <c>GET</c> verb, end of headers (<c>\r\n\r\n</c>), and the case-insensitive
/// header values for <c>Upgrade</c>, <c>Connection</c>, <c>Sec-WebSocket-Version</c> and
/// <c>Sec-WebSocket-Key</c>. The key value is returned as a string (one allocation, the size of
/// a base64-encoded 16-byte nonce — about 24 chars — happens once per connection at handshake time).
/// </summary>
internal static class HandshakeParser
{
    public enum Result
    {
        Incomplete,
        BadRequest,
        Ok,
    }

    public static Result TryParse(in ReadOnlySequence<byte> sequence, out string? secWebSocketKey, out int totalHeaderBytes)
    {
        secWebSocketKey = null;
        totalHeaderBytes = 0;

        // Find end of headers: \r\n\r\n
        var reader = new SequenceReader<byte>(sequence);
        if (!TryFindEndOfHeaders(ref reader, out var headerEndPos))
            return Result.Incomplete;

        var headerSlice = sequence.Slice(sequence.Start, headerEndPos);
        totalHeaderBytes = (int)headerSlice.Length;

        // Materialize headers as a single string. The handshake is one-shot per connection so the
        // alloc is acceptable; in exchange we get straightforward header parsing without
        // hand-rolling a tokenizer over a multi-segment sequence.
        var headerBytes = headerSlice.IsSingleSegment
            ? headerSlice.First.Span
            : (ReadOnlySpan<byte>)headerSlice.ToArray();

        var text = Encoding.ASCII.GetString(headerBytes);
        var lines = text.Split("\r\n");

        if (lines.Length < 1) return Result.BadRequest;

        var requestLine = lines[0];
        if (!requestLine.StartsWith("GET ", StringComparison.OrdinalIgnoreCase))
            return Result.BadRequest;

        bool sawUpgrade = false;
        bool sawConnection = false;
        bool sawVersion = false;
        string? key = null;

        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0) continue;

            var colon = line.IndexOf(':');
            if (colon <= 0) continue;

            var name = line.AsSpan(0, colon).Trim();
            var value = line.AsSpan(colon + 1).Trim();

            if (name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase))
            {
                if (value.Equals("websocket", StringComparison.OrdinalIgnoreCase))
                    sawUpgrade = true;
            }
            else if (name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
            {
                if (value.Contains("upgrade", StringComparison.OrdinalIgnoreCase))
                    sawConnection = true;
            }
            else if (name.Equals("Sec-WebSocket-Version", StringComparison.OrdinalIgnoreCase))
            {
                if (value.Equals("13", StringComparison.Ordinal))
                    sawVersion = true;
            }
            else if (name.Equals("Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase))
            {
                key = value.ToString();
            }
        }

        if (!sawUpgrade || !sawConnection || !sawVersion || string.IsNullOrEmpty(key))
            return Result.BadRequest;

        secWebSocketKey = key;
        return Result.Ok;
    }

    private static bool TryFindEndOfHeaders(ref SequenceReader<byte> reader, out SequencePosition end)
    {
        ReadOnlySpan<byte> needle = "\r\n\r\n"u8;
        if (reader.TryReadTo(out ReadOnlySpan<byte> _, needle, advancePastDelimiter: true))
        {
            end = reader.Position;
            return true;
        }
        end = default;
        return false;
    }
}
