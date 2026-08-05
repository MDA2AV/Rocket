using System.Buffers;
using System.Buffers.Text;
using System.Runtime.InteropServices;

namespace ioxide.httpclient;

/// <summary>
/// One keep-alive HTTP/1.1 connection whose connect, sends and receives all run on the owning
/// reactor's ring and resume inline - the client mirror of the server's connection loop. Sends one
/// request at a time (concurrency comes from the pool's connections, not from pipelining, which
/// real servers handle unevenly).
///
/// Reactor thread only. The send/receive buffers are native and reused across requests; every
/// response gets its own pooled copy, so this connection returns to the pool the moment the
/// response is parsed rather than being pinned until the caller finishes reading.
///
/// C# forbids <c>await</c> inside an unsafe context, so the buffer pointers are touched only by
/// the small non-async helpers below; the async methods stay pure control flow.
/// </summary>
internal sealed class HttpClientConnection : IDisposable
{
    private readonly HttpClientOptions _options;
    private readonly RingSocket _socket;
    private readonly byte[] _hostHeaderLine;

    private nint _send;
    private int _sendCapacity;

    private nint _receive;
    private int _receiveCapacity;
    private int _received;    // bytes held in the receive buffer
    private int _consumed;    // parsed prefix of those bytes

    // Framing of the response being read, filled by ParseHead.
    private long _contentLength = -1;
    private bool _chunked;

    private bool _broken;

    /// <summary>True once the connection can't serve another request (peer closed, protocol error,
    /// or the server asked to close). The pool drops these and opens replacements.</summary>
    public bool IsBroken => _broken;

    private unsafe HttpClientConnection(HttpClientOptions options, RingSocket socket, byte[] hostHeaderLine)
    {
        _options = options;
        _socket = socket;
        _hostHeaderLine = hostHeaderLine;

        _sendCapacity = options.SendBufferSize;
        _send = (nint)NativeMemory.Alloc((nuint)_sendCapacity);

        _receiveCapacity = options.ReceiveBufferSize;
        _receive = (nint)NativeMemory.Alloc((nuint)_receiveCapacity);
    }

    public static async Task<HttpClientConnection> ConnectAsync(IRingHost host, HttpClientOptions options)
    {
        // "host: <authority>\r\n" is fixed for the connection's life - build it once.
        string authority = options.Port is 80 or 443 ? options.Host : $"{options.Host}:{options.Port}";
        byte[] hostHeaderLine = System.Text.Encoding.ASCII.GetBytes($"host: {authority}\r\n");

        RingSocket socket = RingSocket.CreateTcp(host);
        try
        {
            int result = await socket.ConnectAsync(options.Host, options.Port);
            if (result < 0)
            {
                throw new HttpClientException($"connect to {options.Host}:{options.Port} failed: errno {-result}");
            }
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        return new HttpClientConnection(options, socket, hostHeaderLine);
    }

    /// <summary>Send one request and read its response. The response owns its bytes.</summary>
    public async ValueTask<HttpClientResponse> SendAsync(HttpClientRequest request)
    {
        if (_broken)
        {
            throw new HttpClientException("connection is broken");
        }

        try
        {
            await WriteRequestAsync(request);
            return await ReadResponseAsync(request);
        }
        catch
        {
            _broken = true;
            throw;
        }
    }

    // --- request ------------------------------------------------------------------------------

    private async ValueTask WriteRequestAsync(HttpClientRequest request)
    {
        int head = BuildHead(request);

        // A small body rides the same send as the head (one syscall); a large one is sent straight
        // from the caller's memory instead of being copied through the send buffer.
        if (request.Body.Length > 0 && head + request.Body.Length <= _sendCapacity)
        {
            CopyIntoSend(request.Body.Span, head);
            await SendAllAsync(_send, head + request.Body.Length);
            return;
        }

        await SendAllAsync(_send, head);

        if (request.Body.Length > 0)
        {
            using MemoryHandle pinned = request.Body.Pin();
            await SendAllAsync(PointerOf(pinned), request.Body.Length);
        }
    }

    private async ValueTask SendAllAsync(nint buffer, int length)
    {
        int sent = 0;
        while (sent < length)
        {
            int n = await _socket.SendAsync(buffer + sent, length - sent);
            if (n <= 0)
            {
                throw new HttpClientException(n == 0 ? "peer closed while sending" : $"send failed: errno {-n}");
            }
            sent += n;
        }
    }

    // --- response -----------------------------------------------------------------------------

    private async ValueTask<HttpClientResponse> ReadResponseAsync(HttpClientRequest request)
    {
        // Leftovers from the previous response (there should be none) move to the front, so every
        // response parses from offset 0.
        Compact();

        var response = new HttpClientResponse();
        try
        {
            int headEnd = await ReadHeadAsync();
            ParseHead(response, headEnd);
            _consumed = headEnd;

            // 1xx are INTERIM (103 Early Hints, 100 Continue): the real response follows on the
            // same connection. Returning one as final would hand the caller a placeholder and
            // leave the true response buffered for the NEXT request to misread.
            while (response.Status is >= 100 and < 200)
            {
                response.ResetForInterim();
                Compact();
                headEnd = await ReadHeadAsync();
                ParseHead(response, headEnd);
                _consumed = headEnd;
            }

            await ReadBodyAsync(response, headRequest: request.Method.Span.SequenceEqual("HEAD"u8));

            response.Freeze();
            if (response.ConnectionClose)
            {
                _broken = true;   // the pool discards and replaces this connection
            }
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    // Receive until the header terminator is buffered; returns its end offset.
    private async ValueTask<int> ReadHeadAsync()
    {
        int searchFrom = 0;
        while (true)
        {
            int end = IndexOfHeadEnd(searchFrom);
            if (end >= 0)
            {
                return end;
            }
            searchFrom = Math.Max(0, _received - 3);   // the terminator may straddle two reads
            await ReceiveMoreAsync();
        }
    }

    private async ValueTask ReadBodyAsync(HttpClientResponse response, bool headRequest)
    {
        long contentLength = _contentLength;
        bool chunked = _chunked;
        _contentLength = -1;
        _chunked = false;

        // Responses that carry no body whatever the headers say (RFC 9110 §6.4.1).
        if (headRequest || response.Status is 204 or 304)
        {
            response.SetBodyRange((0, 0));
            return;
        }

        if (chunked)
        {
            await ReadChunkedBodyAsync(response);
        }
        else if (contentLength >= 0)
        {
            await ReadFixedBodyAsync(response, contentLength);
        }
        else
        {
            // No framing at all: the body runs until the peer closes (HTTP/1.0 style).
            await ReadUntilCloseAsync(response);
            response.ConnectionClose = true;
        }
    }

    private async ValueTask ReadFixedBodyAsync(HttpClientResponse response, long length)
    {
        if (length > _options.MaxResponseBytes)
        {
            throw new HttpClientException($"response body of {length} exceeds MaxResponseBytes");
        }

        int start = response.ArenaLength;
        long copied = 0;
        while (copied < length)
        {
            if (_consumed == _received)
            {
                await ReceiveMoreAsync();
            }
            copied += AppendAvailable(response, length - copied);
        }

        response.SetBodyRange((start, (int)length));
    }

    private async ValueTask ReadChunkedBodyAsync(HttpClientResponse response)
    {
        int bodyStart = response.ArenaLength;
        long bodyLength = 0;   // long: an int here wraps negative on a hostile chunk size and
                               // slips past the MaxResponseBytes check below

        while (true)
        {
            int lineEnd = await ReadLineAsync();
            if (!TryParseChunkSize(lineEnd, out int chunkSize))
            {
                throw new HttpClientException("malformed chunk size");
            }
            _consumed = lineEnd + 2;

            if (chunkSize == 0)
            {
                // Trailer section (usually empty) through the final blank line.
                while (true)
                {
                    int trailerEnd = await ReadLineAsync();
                    bool blank = trailerEnd == _consumed;
                    _consumed = trailerEnd + 2;
                    if (blank)
                    {
                        break;
                    }
                }
                break;
            }

            bodyLength += chunkSize;
            if (bodyLength > _options.MaxResponseBytes)
            {
                throw new HttpClientException("chunked response exceeds MaxResponseBytes");
            }

            int copied = 0;
            while (copied < chunkSize)
            {
                if (_consumed == _received)
                {
                    await ReceiveMoreAsync();
                }
                copied += AppendAvailable(response, chunkSize - copied);
            }

            _consumed = await ReadLineAsync() + 2;   // the CRLF terminating the chunk
        }

        response.SetBodyRange((bodyStart, (int)bodyLength));
    }

    private async ValueTask ReadUntilCloseAsync(HttpClientResponse response)
    {
        int start = response.ArenaLength;
        int total = AppendAvailable(response, long.MaxValue);

        while (true)
        {
            try
            {
                await ReceiveMoreAsync();
            }
            catch (HttpClientException)
            {
                break;   // peer closed - that IS the framing here
            }

            total += AppendAvailable(response, long.MaxValue);
            if (total > _options.MaxResponseBytes)
            {
                throw new HttpClientException("response body exceeds MaxResponseBytes");
            }
        }

        response.SetBodyRange((start, total));
    }

    // Ensure one CRLF-terminated line is buffered; returns the offset of its CR.
    private async ValueTask<int> ReadLineAsync()
    {
        while (true)
        {
            int marker = IndexOfCrLf();
            if (marker >= 0)
            {
                return marker;
            }
            await ReceiveMoreAsync();
        }
    }

    // One recv appended to the buffer; returns the byte count, compacting or growing as needed.
    private async ValueTask<int> ReceiveMoreAsync()
    {
        EnsureReceiveSpace();

        int n = await _socket.RecvAsync(_receive + _received, _receiveCapacity - _received);
        if (n <= 0)
        {
            throw new HttpClientException(n == 0 ? "peer closed mid-response" : $"recv failed: errno {-n}");
        }
        _received += n;
        return n;
    }

    // --- pointer helpers (no await inside any of these) ---------------------------------------

    private static unsafe nint PointerOf(MemoryHandle handle) => (nint)handle.Pointer;

    private unsafe void CopyIntoSend(ReadOnlySpan<byte> data, int offset)
        => data.CopyTo(new Span<byte>((void*)(_send + offset), _sendCapacity - offset));

    private unsafe int IndexOfHeadEnd(int searchFrom)
    {
        if (_received <= searchFrom)
        {
            return -1;
        }
        int marker = new ReadOnlySpan<byte>((void*)(_receive + searchFrom), _received - searchFrom)
            .IndexOf("\r\n\r\n"u8);
        return marker < 0 ? -1 : searchFrom + marker + 4;
    }

    private unsafe int IndexOfCrLf()
    {
        if (_received <= _consumed)
        {
            return -1;
        }
        int marker = new ReadOnlySpan<byte>((void*)(_receive + _consumed), _received - _consumed)
            .IndexOf("\r\n"u8);
        return marker < 0 ? -1 : _consumed + marker;
    }

    private unsafe bool TryParseChunkSize(int lineEnd, out int size)
    {
        var line = new ReadOnlySpan<byte>((void*)(_receive + _consumed), lineEnd - _consumed);
        int extension = line.IndexOf((byte)';');
        if (extension >= 0)
        {
            line = line[..extension];
        }
        return Utf8Parser.TryParse(line, out size, out _, 'x');
    }

    /// <summary>Copy up to <paramref name="limit"/> unconsumed bytes into the response's arena;
    /// returns how many moved.</summary>
    private unsafe int AppendAvailable(HttpClientResponse response, long limit)
    {
        int take = (int)Math.Min(limit, _received - _consumed);
        if (take <= 0)
        {
            return 0;
        }

        response.Append(new ReadOnlySpan<byte>((void*)(_receive + _consumed), take));
        _consumed += take;
        return take;
    }

    private unsafe void ParseHead(HttpClientResponse response, int headEnd)
    {
        var head = new ReadOnlySpan<byte>((void*)_receive, headEnd);

        int lineEnd = head.IndexOf("\r\n"u8);
        if (lineEnd < 0)
        {
            throw new HttpClientException("malformed status line");
        }

        ReadOnlySpan<byte> statusLine = head[..lineEnd];
        if (statusLine.Length < 12 || !statusLine.StartsWith("HTTP/1."u8))
        {
            throw new HttpClientException("not an HTTP/1.x response");
        }

        bool http10 = statusLine[7] == (byte)'0';
        if (!Utf8Parser.TryParse(statusLine[9..12], out int status, out _))
        {
            throw new HttpClientException("malformed status code");
        }
        response.Status = status;

        bool sawClose = false;
        bool sawKeepAlive = false;

        int cursor = lineEnd + 2;
        while (cursor < head.Length)
        {
            ReadOnlySpan<byte> rest = head[cursor..];
            int end = rest.IndexOf("\r\n"u8);
            if (end <= 0)
            {
                break;   // the blank line ending the block
            }

            ReadOnlySpan<byte> line = rest[..end];
            cursor += end + 2;

            int colon = line.IndexOf((byte)':');
            if (colon <= 0)
            {
                continue;   // tolerate a junk line rather than failing the response
            }

            ReadOnlySpan<byte> name = line[..colon];
            ReadOnlySpan<byte> value = line[(colon + 1)..].Trim((byte)' ');

            (int Offset, int Length) nameRange = response.Append(name);
            response.LowercaseArena(nameRange);
            (int Offset, int Length) valueRange = response.Append(value);
            response.AddHeaderRange(nameRange, valueRange);

            if (EqualsIgnoreCase(name, "connection"u8))
            {
                sawClose |= ContainsToken(value, "close"u8);
                sawKeepAlive |= ContainsToken(value, "keep-alive"u8);
            }
            else if (EqualsIgnoreCase(name, "content-length"u8))
            {
                if (Utf8Parser.TryParse(value, out long declared, out _))
                {
                    _contentLength = declared;
                }
            }
            else if (EqualsIgnoreCase(name, "transfer-encoding"u8))
            {
                _chunked |= ContainsToken(value, "chunked"u8);
            }
        }

        // HTTP/1.1 keeps alive unless told otherwise; HTTP/1.0 is the reverse.
        response.ConnectionClose = http10 ? !sawKeepAlive : sawClose;
    }

    private unsafe void Compact()
    {
        if (_consumed == 0)
        {
            return;
        }
        int remaining = _received - _consumed;
        if (remaining > 0)
        {
            Buffer.MemoryCopy((void*)(_receive + _consumed), (void*)_receive, _receiveCapacity, remaining);
        }
        _received = remaining;
        _consumed = 0;
    }

    private unsafe void EnsureReceiveSpace()
    {
        if (_received < _receiveCapacity)
        {
            return;
        }

        Compact();
        if (_received < _receiveCapacity)
        {
            return;
        }

        if (_receiveCapacity >= _options.MaxResponseBytes)
        {
            throw new HttpClientException("response exceeds MaxResponseBytes");
        }
        _receiveCapacity = Math.Min(_receiveCapacity * 2, _options.MaxResponseBytes);
        _receive = (nint)NativeMemory.Realloc((void*)_receive, (nuint)_receiveCapacity);
    }

    private unsafe int BuildHead(HttpClientRequest request)
    {
        int needed = request.Method.Length + request.Path.Length + _hostHeaderLine.Length + 64;
        foreach (KeyValuePair<ReadOnlyMemory<byte>, ReadOnlyMemory<byte>> field in request.Headers.AsSpan())
        {
            needed += field.Key.Length + field.Value.Length + 4;
        }
        if (needed > _sendCapacity)
        {
            _sendCapacity = needed;
            _send = (nint)NativeMemory.Realloc((void*)_send, (nuint)_sendCapacity);
        }

        var buffer = new Span<byte>((void*)_send, _sendCapacity);
        int cursor = 0;

        request.Method.Span.CopyTo(buffer[cursor..]);
        cursor += request.Method.Length;
        buffer[cursor++] = (byte)' ';

        request.Path.Span.CopyTo(buffer[cursor..]);
        cursor += request.Path.Length;

        " HTTP/1.1\r\n"u8.CopyTo(buffer[cursor..]);
        cursor += 11;

        _hostHeaderLine.CopyTo(buffer[cursor..]);
        cursor += _hostHeaderLine.Length;

        if (request.Body.Length > 0)
        {
            "content-length: "u8.CopyTo(buffer[cursor..]);
            cursor += 16;
            Utf8Formatter.TryFormat(request.Body.Length, buffer[cursor..], out int written);
            cursor += written;
            "\r\n"u8.CopyTo(buffer[cursor..]);
            cursor += 2;
        }

        foreach (KeyValuePair<ReadOnlyMemory<byte>, ReadOnlyMemory<byte>> field in request.Headers.AsSpan())
        {
            field.Key.Span.CopyTo(buffer[cursor..]);
            cursor += field.Key.Length;
            ": "u8.CopyTo(buffer[cursor..]);
            cursor += 2;
            field.Value.Span.CopyTo(buffer[cursor..]);
            cursor += field.Value.Length;
            "\r\n"u8.CopyTo(buffer[cursor..]);
            cursor += 2;
        }

        "\r\n"u8.CopyTo(buffer[cursor..]);
        return cursor + 2;
    }

    private static bool EqualsIgnoreCase(ReadOnlySpan<byte> left, ReadOnlySpan<byte> lowercaseRight)
    {
        if (left.Length != lowercaseRight.Length)
        {
            return false;
        }
        for (int i = 0; i < left.Length; i++)
        {
            byte b = left[i];
            if (b is >= (byte)'A' and <= (byte)'Z')
            {
                b = (byte)(b | 0x20);
            }
            if (b != lowercaseRight[i])
            {
                return false;
            }
        }
        return true;
    }

    // Case-insensitive comma-separated token match ("keep-alive, Upgrade" contains "keep-alive").
    private static bool ContainsToken(ReadOnlySpan<byte> value, ReadOnlySpan<byte> lowercaseToken)
    {
        while (!value.IsEmpty)
        {
            int comma = value.IndexOf((byte)',');
            ReadOnlySpan<byte> token = (comma < 0 ? value : value[..comma]).Trim((byte)' ');
            if (EqualsIgnoreCase(token, lowercaseToken))
            {
                return true;
            }
            if (comma < 0)
            {
                break;
            }
            value = value[(comma + 1)..];
        }
        return false;
    }

    public unsafe void Dispose()
    {
        _broken = true;
        _socket.Dispose();
        if (_send != 0)
        {
            NativeMemory.Free((void*)_send);
            _send = 0;
        }
        if (_receive != 0)
        {
            NativeMemory.Free((void*)_receive);
            _receive = 0;
        }
    }
}

/// <summary>Any client-side failure: connect, send, recv, or a malformed response.</summary>
public sealed class HttpClientException : Exception
{
    public HttpClientException(string message) : base(message) { }
}
