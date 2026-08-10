using System.Buffers;
using System.Buffers.Binary;
using System.Threading.Tasks.Sources;
using ioxide.http2;

namespace ioxide.httpclient;

/// <summary>
/// One HTTP/2 connection over a ring socket: many requests in flight at once, multiplexed onto a
/// single TCP byte stream.
///
/// Framing, HPACK and flow control are ioxide.http2's - the same managed code the server side runs,
/// pointed the other way round. What is here is the client's half of the protocol: the preface, odd
/// stream ids, requests instead of responses, and the retry rules that decide whether a failed
/// exchange may be sent again.
///
/// This speaks <b>h2c with prior knowledge</b> (RFC 9113 section 3.3) on cleartext and <b>h2 by
/// ALPN</b> over TLS. There is no Upgrade dance in either direction: on a plaintext port the origin
/// must already be expecting HTTP/2.
/// </summary>
/// <remarks>
/// Reactor thread only. Completions are recorded while frames are being parsed and resumed after
/// the parse unwinds, so a caller that immediately submits another request - or retries through the
/// pool, which may dispose this connection - never re-enters the parser from inside itself.
/// </remarks>
public sealed class Http2ClientConnection : IDisposable
{
    private const int IngressBufferSize = 64 * 1024;

    /// <summary>What we advertise per stream. Large, because the origin is the one streaming to us.</summary>
    private const int SelfInitialWindow = 1 << 20;

    private readonly IClientTransport _transport;
    private readonly byte[] _authority;
    private readonly int _maxResponseBytes;

    private readonly Dictionary<int, PendingRequest> _pending = new();
    private readonly HpackDecoder _decoder = new();
    private byte[] _headerScratch = [];

    // Client-initiated streams are odd and strictly increasing (RFC 9113 section 5.1.1).
    private int _nextStreamId = 1;

    // Ingress accumulator. A frame boundary has nothing to do with a recv boundary, so a partial
    // frame has to survive to the next read.
    private byte[] _inbound = [];
    private int _inboundUsed;

    // Egress staging, double-buffered: a drain swaps the buffers and sends the full one, so bytes
    // staged while that send is awaited land in the empty one and keep their order.
    private byte[] _egress = new byte[16 * 1024];
    private byte[] _sending = new byte[16 * 1024];
    private int _egressUsed;

    // Peer flow-control state. The connection window and per-stream windows both have to allow a
    // DATA frame before it may go out.
    private int _peerConnectionWindow = 65535;
    private int _peerInitialStreamWindow = 65535;
    private int _peerMaxFrameSize = 16384;

    private bool _failed;
    private bool _disposed;

    // Set when the peer stops accepting new streams (GOAWAY, or a max-requests limit). What is in
    // flight can still finish; the pool must stop handing this connection new work.
    private bool _retiring;

    // Completions and stream failures recorded during the current parse, resumed once it unwinds.
    private readonly List<(PendingRequest Pending, HttpClientResponse? Response, Exception? Error)> _completedThisPass = [];

    // Set while the pump owns the drain, so a request submitted by a resumed caller rides the
    // pump's own egress pass instead of issuing its own.
    private bool _inPumpPass;

    // One drain at a time. Both the pump and SendAsync can start one, and a drain awaits socket
    // sends - so without this two of them interleave and put a corrupt frame stream on the wire.
    private bool _draining;
    private bool _drainAgain;

    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Http2ClientConnection(IClientTransport transport, string authority, int maxResponseBytes)
    {
        _transport = transport;
        _authority = System.Text.Encoding.ASCII.GetBytes(authority);
        _maxResponseBytes = maxResponseBytes;
    }

    public bool IsBroken => _failed || _disposed || _retiring;

    public int InFlight => _pending.Count;

    public static async Task<Http2ClientConnection> ConnectAsync(IRingHost host, string ip, ushort port,
        string authority, TlsClientContext? tls = null, int maxResponseBytes = 8 * 1024 * 1024)
    {
        IClientTransport transport = await ClientTransport.ConnectAsync(host, ip, port, tls);

        // Over TLS, h2 is chosen by ALPN and nothing else. An origin that selected http/1.1 (or
        // offered no ALPN at all) is not going to understand the HTTP/2 preface, and sending it
        // anyway produces a connection that hangs rather than an error worth reading.
        if (tls is not null && transport.NegotiatedAlpn != "h2")
        {
            string selected = transport.NegotiatedAlpn ?? "none";
            transport.Dispose();
            throw new Http2ClientException(
                $"{ip}:{port} did not negotiate h2 over ALPN (selected: {selected})");
        }

        var connection = new Http2ClientConnection(transport, authority, maxResponseBytes);
        connection.WritePrefaceAndSettings();
        _ = connection.PumpLoopAsync();
        await connection._ready.Task;   // preface + SETTINGS on the wire before the first request
        return connection;
    }

    // --- requests -------------------------------------------------------------------------------

    public ValueTask<HttpClientResponse> SendAsync(HttpClientRequest request)
    {
        if (IsBroken)
        {
            // Nothing was submitted, so resending elsewhere is unconditionally safe - surfaced as
            // the retryable kind so the pool moves the request to a replacement connection.
            throw new Http2StreamRefusedException("connection is retiring or closed; request was not submitted");
        }

        int streamId = _nextStreamId;
        _nextStreamId += 2;

        var pending = new PendingRequest
        {
            StreamId = streamId,
            SendWindow = _peerInitialStreamWindow,
            BodyRemaining = request.Body,
        };
        _pending[streamId] = pending;

        WriteRequestHeaders(request, streamId, endStream: request.Body.Length == 0);
        PumpBody(pending);

        if (!_inPumpPass)
        {
            _ = DrainDetachedAsync();   // issued from outside a pass: put it on the wire now
        }

        return pending.Task;
    }

    // The detached drain SendAsync fires. Swallowing its failure would strand every in-flight
    // waiter until the pump happens to notice (_failed is set, but the pump sits in recv): fail
    // them now and close the socket, which errors that recv out and lets the pump exit.
    private async Task DrainDetachedAsync()
    {
        try
        {
            await PumpEgressAsync();
        }
        catch (Exception e)
        {
            FailAll(new Http2ClientException($"egress drain failed: {e.GetBaseException().Message}"));
            Dispose();
        }
    }

    /// <summary>
    /// The request's field section, HPACK-encoded into one HEADERS frame.
    /// </summary>
    /// <remarks>
    /// The encoder never touches the dynamic table, so it holds no per-connection compression state
    /// and cannot desynchronise from the origin's decoder. On a client that costs more than it does
    /// on a server - a client repeats its own header set on every request, which is exactly what
    /// indexing is for - but the pseudo-headers that dominate a small request are static-table hits
    /// either way, and a desynchronised HPACK table poisons every later stream on the connection.
    /// </remarks>
    private void WriteRequestHeaders(HttpClientRequest request, int streamId, bool endStream)
    {
        int capacity = HpackEncoder.MaxEncodedLength(":authority".Length, _authority.Length)
                     + HpackEncoder.MaxEncodedLength(":method".Length, request.Method.Length)
                     + HpackEncoder.MaxEncodedLength(":path".Length, request.Path.Length)
                     + HpackEncoder.MaxEncodedLength(":scheme".Length, 5);
        foreach (KeyValuePair<ReadOnlyMemory<byte>, ReadOnlyMemory<byte>> field in request.Headers.AsSpan())
        {
            capacity += HpackEncoder.MaxEncodedLength(field.Key.Length, field.Value.Length);
        }

        byte[] block = ArrayPool<byte>.Shared.Rent(capacity);
        int used = 0;

        // Pseudo-headers first and in this order, per RFC 9113 section 8.3.1.
        used += HpackEncoder.Encode(block.AsSpan(used), ":method"u8, request.Method.Span);
        // :scheme must match the transport. An origin reached over TLS sees ":scheme http" as a
        // mismatch, and the strict ones reject the stream for it.
        used += HpackEncoder.Encode(block.AsSpan(used), ":scheme"u8, _transport.IsSecure ? "https"u8 : "http"u8);
        used += HpackEncoder.Encode(block.AsSpan(used), ":authority"u8, _authority);
        used += HpackEncoder.Encode(block.AsSpan(used), ":path"u8, request.Path.Span);

        foreach (KeyValuePair<ReadOnlyMemory<byte>, ReadOnlyMemory<byte>> field in request.Headers.AsSpan())
        {
            used += HpackEncoder.Encode(block.AsSpan(used), field.Key.Span, field.Value.Span);
        }

        WriteHeaderBlock(streamId, block.AsSpan(0, used), endStream);
        ArrayPool<byte>.Shared.Return(block);
    }

    // One HEADERS, then CONTINUATION for whatever did not fit the peer's frame size. The block
    // cannot be split anywhere else: HPACK is a stream, so a decoder needs the pieces contiguous
    // and uninterrupted by any other stream's frames.
    private void WriteHeaderBlock(int streamId, ReadOnlySpan<byte> block, bool endStream)
    {
        int first = Math.Min(block.Length, _peerMaxFrameSize);
        bool complete = first == block.Length;

        FrameFlags flags = FrameFlags.None;
        if (complete)  flags |= FrameFlags.EndHeaders;
        if (endStream) flags |= FrameFlags.EndStream;

        Span<byte> header = stackalloc byte[FrameHeader.Size];
        new FrameHeader(first, FrameType.Headers, flags, streamId).Write(header);
        Stage(header);
        Stage(block[..first]);

        int offset = first;
        while (offset < block.Length)
        {
            int chunk = Math.Min(block.Length - offset, _peerMaxFrameSize);
            bool last = offset + chunk == block.Length;
            new FrameHeader(chunk, FrameType.Continuation,
                last ? FrameFlags.EndHeaders : FrameFlags.None, streamId).Write(header);
            Stage(header);
            Stage(block.Slice(offset, chunk));
            offset += chunk;
        }
    }

    /// <summary>
    /// Push as much of the request body as the peer's windows allow. Called at submit and again on
    /// every WINDOW_UPDATE, because credit is what decides when the rest may go - a body larger
    /// than the initial 65535-byte window leaves in pieces as the origin reads it.
    /// </summary>
    private void PumpBody(PendingRequest pending)
    {
        while (pending.BodyRemaining.Length > 0)
        {
            int credit = Math.Min(pending.SendWindow, _peerConnectionWindow);
            if (credit <= 0)
            {
                return;   // parked; a WINDOW_UPDATE brings us back
            }

            int chunk = Math.Min(Math.Min(credit, _peerMaxFrameSize), pending.BodyRemaining.Length);
            bool last = chunk == pending.BodyRemaining.Length;

            Span<byte> header = stackalloc byte[FrameHeader.Size];
            new FrameHeader(chunk, FrameType.Data, last ? FrameFlags.EndStream : FrameFlags.None,
                pending.StreamId).Write(header);
            Stage(header);
            Stage(pending.BodyRemaining.Span[..chunk]);

            pending.BodyRemaining = pending.BodyRemaining[chunk..];
            pending.SendWindow -= chunk;
            _peerConnectionWindow -= chunk;
        }
    }

    // --- the pump -------------------------------------------------------------------------------

    private async Task PumpLoopAsync()
    {
        nint receive = System.Runtime.InteropServices.Marshal.AllocHGlobal(IngressBufferSize);
        try
        {
            await PumpEgressAsync();      // connection preface + SETTINGS
            _ready.TrySetResult(true);

            while (!_failed && !_disposed)
            {
                int n = await _transport.RecvAsync(receive, IngressBufferSize);
                if (n <= 0)
                {
                    throw new Http2ClientException(n == 0 ? "peer closed the connection" : $"recv failed: errno {-n}");
                }

                Accumulate(receive, n);

                _inPumpPass = true;
                try
                {
                    ParseAvailable();

                    // The parser has unwound: safe to resume the waiters. Their follow-up requests
                    // submit while _inPumpPass is set, so they all ride the drain below.
                    CompleteFinishedRequests();
                }
                finally
                {
                    _inPumpPass = false;
                }

                await PumpEgressAsync();   // ACKs, WINDOW_UPDATEs and any newly submitted requests

                if (_retiring && _pending.Count == 0)
                {
                    break;   // GOAWAY drained and nothing left in flight; the pool opens a replacement
                }
            }
        }
        catch (Exception e)
        {
            FailAll(new Http2ClientException($"connection pump failed: {e.GetBaseException().Message}"));
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(receive);

            // A clean retirement (GOAWAY drained, nothing left to read) leaves only streams the
            // peer never processed: ones past last_stream_id or never sent. RFC 9113 8.7 makes
            // resending those safe, so they fail retryable and the pool resends them. Anything
            // else is a real connection failure.
            FailAll(_retiring && !_failed
                ? new Http2StreamRefusedException("connection retired (GOAWAY); request was not processed")
                : new Http2ClientException("connection closed"));
            Dispose();
        }
    }

    private unsafe void Accumulate(nint receive, int length)
    {
        if (_inbound.Length - _inboundUsed < length)
        {
            long size = Math.Max(IngressBufferSize, (long)_inbound.Length * 2);
            while (size < (long)_inboundUsed + length)
            {
                size *= 2;
            }
            byte[] grown = ArrayPool<byte>.Shared.Rent((int)Math.Min(size, Array.MaxLength));
            _inbound.AsSpan(0, _inboundUsed).CopyTo(grown);
            if (_inbound.Length > 0)
            {
                ArrayPool<byte>.Shared.Return(_inbound);
            }
            _inbound = grown;
        }

        new ReadOnlySpan<byte>((void*)receive, length).CopyTo(_inbound.AsSpan(_inboundUsed));
        _inboundUsed += length;
    }

    private async ValueTask PumpEgressAsync()
    {
        if (_draining)
        {
            _drainAgain = true;   // a drain is mid-flight; it will pick up what we just staged
            return;
        }

        _draining = true;
        try
        {
            await DrainLoopAsync();

            while (_drainAgain)
            {
                _drainAgain = false;
                await DrainLoopAsync();
            }
        }
        finally
        {
            _draining = false;
        }
    }

    private async ValueTask DrainLoopAsync()
    {
        while (_egressUsed > 0 && !_disposed)
        {
            // Swap rather than copy: whatever is staged during the await below lands in the buffer
            // this one just vacated, so ordering holds without a second pass over the bytes.
            (_egress, _sending) = (_sending, _egress);
            int length = _egressUsed;
            _egressUsed = 0;

            await SendAllAsync(_sending, length);
        }
    }

    private async ValueTask SendAllAsync(byte[] buffer, int length)
    {
        using MemoryHandle pinned = buffer.AsMemory().Pin();
        nint basePointer = PointerOf(pinned);

        int sent = 0;
        while (sent < length)
        {
            int n = await _transport.SendAsync(basePointer + sent, length - sent);
            if (n <= 0)
            {
                _failed = true;
                throw new Http2ClientException(n == 0 ? "peer closed while sending" : $"send failed: errno {-n}");
            }
            sent += n;
        }
    }

    private static unsafe nint PointerOf(MemoryHandle pinned) => (nint)pinned.Pointer;

    private void Stage(ReadOnlySpan<byte> bytes)
    {
        if (_disposed)
        {
            return;
        }

        if (_egress.Length - _egressUsed < bytes.Length)
        {
            long size = Math.Max(16 * 1024, (long)_egress.Length * 2);
            while (size < (long)_egressUsed + bytes.Length)
            {
                size *= 2;
            }
            byte[] grown = new byte[(int)Math.Min(size, Array.MaxLength)];
            _egress.AsSpan(0, _egressUsed).CopyTo(grown);
            _egress = grown;
        }

        bytes.CopyTo(_egress.AsSpan(_egressUsed));
        _egressUsed += bytes.Length;
    }

    // --- frames out -----------------------------------------------------------------------------

    private void WritePrefaceAndSettings()
    {
        Stage(FrameHeader.ClientPreface);

        // ENABLE_PUSH off: this client has no cache to push into, and refusing it here is simpler
        // than answering PUSH_PROMISE frames. INITIAL_WINDOW_SIZE is ours to set - the origin is
        // the side sending bodies, so a small window here would throttle every download.
        Span<byte> frame = stackalloc byte[FrameHeader.Size + 12];
        new FrameHeader(12, FrameType.Settings, FrameFlags.None, 0).Write(frame);
        BinaryPrimitives.WriteUInt16BigEndian(frame[9..], Http2Setting.EnablePush);
        BinaryPrimitives.WriteUInt32BigEndian(frame[11..], 0);
        BinaryPrimitives.WriteUInt16BigEndian(frame[15..], Http2Setting.InitialWindowSize);
        BinaryPrimitives.WriteUInt32BigEndian(frame[17..], SelfInitialWindow);
        Stage(frame);

        // Lift the connection window to match what we just advertised per stream. The connection
        // window is NOT covered by SETTINGS_INITIAL_WINDOW_SIZE and stays at 65535 otherwise, which
        // would cap every download on the connection no matter what the streams allow.
        WriteWindowUpdate(0, SelfInitialWindow - 65535);
    }

    private void WriteSettingsAck()
    {
        Span<byte> frame = stackalloc byte[FrameHeader.Size];
        new FrameHeader(0, FrameType.Settings, FrameFlags.Ack, 0).Write(frame);
        Stage(frame);
    }

    private void WriteWindowUpdate(int streamId, int increment)
    {
        if (increment <= 0)
        {
            return;
        }

        Span<byte> frame = stackalloc byte[FrameHeader.Size + 4];
        new FrameHeader(4, FrameType.WindowUpdate, FrameFlags.None, streamId).Write(frame);
        BinaryPrimitives.WriteUInt32BigEndian(frame[FrameHeader.Size..], (uint)increment);
        Stage(frame);
    }

    private void WritePingAck(ReadOnlySpan<byte> opaque)
    {
        Span<byte> frame = stackalloc byte[FrameHeader.Size + 8];
        new FrameHeader(8, FrameType.Ping, FrameFlags.Ack, 0).Write(frame);
        opaque.CopyTo(frame[FrameHeader.Size..]);
        Stage(frame);
    }

    private void GoAway(uint error)
    {
        Span<byte> frame = stackalloc byte[FrameHeader.Size + 8];
        new FrameHeader(8, FrameType.GoAway, FrameFlags.None, 0).Write(frame);
        BinaryPrimitives.WriteUInt32BigEndian(frame[FrameHeader.Size..], 0);
        BinaryPrimitives.WriteUInt32BigEndian(frame[(FrameHeader.Size + 4)..], error);
        Stage(frame);
        _failed = true;
    }

    // --- frames in ------------------------------------------------------------------------------

    private void ParseAvailable()
    {
        int position = 0;

        while (!_failed)
        {
            if (!FrameHeader.TryRead(_inbound.AsSpan(position, _inboundUsed - position), out FrameHeader header))
            {
                break;   // not even a header yet
            }

            int total = FrameHeader.Size + header.Length;
            if (_inboundUsed - position < total)
            {
                break;   // header is in, payload is not
            }

            Handle(header, _inbound.AsSpan(position + FrameHeader.Size, header.Length));
            position += total;
        }

        Compact(position);
    }

    private void Compact(int consumed)
    {
        if (consumed == 0)
        {
            return;
        }

        int remaining = _inboundUsed - consumed;
        if (remaining > 0)
        {
            _inbound.AsSpan(consumed, remaining).CopyTo(_inbound);
        }
        _inboundUsed = remaining;
    }

    private void Handle(in FrameHeader header, ReadOnlySpan<byte> payload)
    {
        switch (header.Type)
        {
            case FrameType.Headers:      HandleHeaders(header, payload); break;
            case FrameType.Continuation: HandleContinuation(header, payload); break;
            case FrameType.Data:         HandleData(header, payload); break;
            case FrameType.Settings:     HandleSettings(header, payload); break;
            case FrameType.WindowUpdate: HandleWindowUpdate(header, payload); break;
            case FrameType.Ping:         HandlePing(header, payload); break;
            case FrameType.RstStream:    HandleRstStream(header, payload); break;
            case FrameType.GoAway:       HandleGoAway(payload); break;

            // We advertised ENABLE_PUSH=0, so a PUSH_PROMISE is the origin breaking the setting we
            // gave it. PRIORITY carries no obligation and is ignored.
            case FrameType.PushPromise:
                GoAway(Http2Error.ProtocolError);
                break;

            case FrameType.Priority:
            default:
                break;
        }
    }

    private void HandleHeaders(in FrameHeader header, ReadOnlySpan<byte> payload)
    {
        ReadOnlySpan<byte> block = payload;

        // Padding and priority sit in front of the header block and are not part of it.
        if ((header.Flags & FrameFlags.Padded) != 0)
        {
            if (block.Length < 1)
            {
                GoAway(Http2Error.FrameSizeError);
                return;
            }
            int padding = block[0];
            block = block[1..];
            if (padding > block.Length)
            {
                GoAway(Http2Error.ProtocolError);
                return;
            }
            block = block[..^padding];
        }

        if ((header.Flags & FrameFlags.Priority) != 0)
        {
            if (block.Length < 5)
            {
                GoAway(Http2Error.FrameSizeError);
                return;
            }
            block = block[5..];
        }

        // A response on a stream we no longer have (cancelled, or already complete) still has to be
        // decoded: HPACK is stateful across the whole connection, so skipping the block would
        // desynchronise the table for every stream after it.
        if (_pending.TryGetValue(header.StreamId, out PendingRequest? pending))
        {
            pending.AppendHeaderBlock(block);
        }
        else
        {
            _orphanBlock.AppendHeaderBlock(block);
        }

        // Recorded rather than acted on: END_STREAM is carried by the HEADERS that OPENS the block,
        // but the block may run on into CONTINUATION frames. Completing here would hand back a
        // response whose header fields have not been decoded yet.
        if ((header.Flags & FrameFlags.EndStream) != 0 && pending is not null)
        {
            pending.HeadersEndedStream = true;
        }

        if ((header.Flags & FrameFlags.EndHeaders) != 0)
        {
            if (!DecodeHeaderBlock(pending))
            {
                return;
            }

            if (pending is { HeadersEndedStream: true })
            {
                Complete(pending);
            }
        }
    }

    private void HandleContinuation(in FrameHeader header, ReadOnlySpan<byte> payload)
    {
        _pending.TryGetValue(header.StreamId, out PendingRequest? pending);
        (pending ?? _orphanBlock).AppendHeaderBlock(payload);

        if ((header.Flags & FrameFlags.EndHeaders) != 0)
        {
            if (!DecodeHeaderBlock(pending))
            {
                return;
            }

            // CONTINUATION carries no END_STREAM of its own; it is on the HEADERS that opened the
            // block, which we recorded there.
            if (pending is { HeadersEndedStream: true })
            {
                Complete(pending);
            }
        }
    }

    /// <summary>Header blocks for streams we no longer track, decoded only to keep HPACK in step.</summary>
    private readonly PendingRequest _orphanBlock = new();

    private bool DecodeHeaderBlock(PendingRequest? pending)
    {
        PendingRequest target = pending ?? _orphanBlock;
        try
        {
            ReadOnlySpan<byte> block = target.HeaderBlock;

            // Huffman can expand a literal well past its encoded length, so the scratch has to be
            // able to hold the worst case for the whole block.
            int needed = Huffman.MaxDecodedLength(block.Length) + block.Length;
            if (_headerScratch.Length < needed)
            {
                _headerScratch = new byte[needed];
            }

            if (pending is null)
            {
                _decoder.Decode(block, _headerScratch, static (_, _) => { });
            }
            else
            {
                HttpClientResponse response = pending.Response ??= NewResponse();
                _decoder.Decode(block, _headerScratch, (name, value) => AddHeader(response, name, value));
                pending.Assembly.EndFieldSection(response);
            }

            target.ClearHeaderBlock();
            return true;
        }
        catch (HpackDecoder.HpackException)
        {
            // The dynamic tables have diverged; every later block on this connection would decode
            // to nonsense, so the connection - not the stream - is what has to end.
            GoAway(Http2Error.CompressionError);
            return false;
        }
    }

    private HttpClientResponse NewResponse()
    {
        var response = new HttpClientResponse();
        response.SetMaxArenaBytes(_maxResponseBytes);
        return response;
    }

    private static void AddHeader(HttpClientResponse response, ReadOnlySpan<byte> name, ReadOnlySpan<byte> value)
    {
        // ":status" is the status line's stand-in; everything else is an ordinary field.
        if (name.SequenceEqual(":status"u8))
        {
            int status = 0;
            foreach (byte digit in value)
            {
                status = (status * 10) + (digit - (byte)'0');
            }
            response.Status = status;
            return;
        }

        (int Offset, int Length) nameRange = response.Append(name);
        (int Offset, int Length) valueRange = response.Append(value);
        response.AddHeaderRange(nameRange, valueRange);
    }

    private void HandleData(in FrameHeader header, ReadOnlySpan<byte> payload)
    {
        ReadOnlySpan<byte> body = payload;

        if ((header.Flags & FrameFlags.Padded) != 0)
        {
            if (body.Length < 1)
            {
                GoAway(Http2Error.FrameSizeError);
                return;
            }
            int padding = body[0];
            body = body[1..];
            if (padding > body.Length)
            {
                GoAway(Http2Error.ProtocolError);
                return;
            }
            body = body[..^padding];
        }

        if (_pending.TryGetValue(header.StreamId, out PendingRequest? pending) &&
            pending.Response is { } response)
        {
            // Count what the arena ACCEPTED, not what arrived: past MaxResponseBytes the append is
            // refused and returns a zero-length range, and a body length that outran the arena
            // would make Freeze slice out of bounds.
            pending.Assembly.BodyLength += response.Append(body).Length;
        }

        // The whole payload counts against the window, padding included, so the peer's accounting
        // and ours agree. Replenished immediately: this client buffers the response anyway, so
        // holding the window back would only stall the origin.
        if (payload.Length > 0)
        {
            WriteWindowUpdate(0, payload.Length);
            if (header.StreamId != 0 && pending is not null)
            {
                WriteWindowUpdate(header.StreamId, payload.Length);
            }
        }

        if ((header.Flags & FrameFlags.EndStream) != 0 && pending is not null)
        {
            Complete(pending);
        }
    }

    private void Complete(PendingRequest pending)
    {
        if (!_pending.Remove(pending.StreamId))
        {
            return;
        }

        HttpClientResponse response = pending.Response ?? new HttpClientResponse();
        response.SetBodyRange(pending.Assembly.BodyRange);
        response.Freeze();

        // Record only - resumed by CompleteFinishedRequests once the parse unwinds.
        _completedThisPass.Add((pending, response, null));
    }

    private void HandleSettings(in FrameHeader header, ReadOnlySpan<byte> payload)
    {
        if ((header.Flags & FrameFlags.Ack) != 0)
        {
            return;   // our own SETTINGS acknowledged
        }

        if (payload.Length % 6 != 0)
        {
            GoAway(Http2Error.FrameSizeError);
            return;
        }

        for (int offset = 0; offset + 6 <= payload.Length; offset += 6)
        {
            ushort id = BinaryPrimitives.ReadUInt16BigEndian(payload[offset..]);
            uint value = BinaryPrimitives.ReadUInt32BigEndian(payload[(offset + 2)..]);

            switch (id)
            {
                case Http2Setting.InitialWindowSize:
                    if (value > int.MaxValue)
                    {
                        GoAway(Http2Error.FlowControlError);
                        return;
                    }
                    // Applies retroactively to every open stream, per RFC 9113 section 6.9.2.
                    int delta = (int)value - _peerInitialStreamWindow;
                    _peerInitialStreamWindow = (int)value;
                    foreach (PendingRequest stream in _pending.Values)
                    {
                        stream.SendWindow += delta;
                    }
                    break;

                case Http2Setting.MaxFrameSize:
                    if (value is < 16384 or > 16777215)
                    {
                        GoAway(Http2Error.ProtocolError);
                        return;
                    }
                    _peerMaxFrameSize = (int)value;
                    break;
            }
        }

        WriteSettingsAck();

        // A raised window may have unparked a body mid-send.
        PumpParkedBodies();
    }

    private void HandleWindowUpdate(in FrameHeader header, ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 4)
        {
            GoAway(Http2Error.FrameSizeError);
            return;
        }

        int increment = (int)(BinaryPrimitives.ReadUInt32BigEndian(payload) & 0x7FFFFFFF);
        if (increment == 0)
        {
            GoAway(Http2Error.ProtocolError);
            return;
        }

        if (header.StreamId == 0)
        {
            _peerConnectionWindow += increment;
            PumpParkedBodies();   // connection credit unparks every stream, not one
        }
        else if (_pending.TryGetValue(header.StreamId, out PendingRequest? pending))
        {
            pending.SendWindow += increment;
            PumpBody(pending);
        }
    }

    private void PumpParkedBodies()
    {
        foreach (PendingRequest pending in _pending.Values)
        {
            if (pending.BodyRemaining.Length > 0)
            {
                PumpBody(pending);
            }
        }
    }

    private void HandlePing(in FrameHeader header, ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 8)
        {
            GoAway(Http2Error.FrameSizeError);
            return;
        }
        if ((header.Flags & FrameFlags.Ack) != 0)
        {
            return;
        }

        WritePingAck(payload);
    }

    private void HandleRstStream(in FrameHeader header, ReadOnlySpan<byte> payload)
    {
        uint error = payload.Length >= 4 ? BinaryPrimitives.ReadUInt32BigEndian(payload) : Http2Error.NoError;

        // REFUSED_STREAM means the peer never processed the request - it is retiring the connection
        // (GOAWAY, or a max-requests limit like nginx's keepalive_requests). RFC 9113 8.7 makes
        // retrying it explicitly safe, so it is surfaced as retryable and the connection retires.
        bool refused = error == Http2Error.RefusedStream;
        if (refused)
        {
            _retiring = true;
        }

        if (_pending.Remove(header.StreamId, out PendingRequest? pending))
        {
            // Record only, exactly like a completion: failing here would resume the waiter mid-parse,
            // and a resumed caller retries through the pool, which prunes retiring connections -
            // disposing this one while its own parser is still on the stack.
            _completedThisPass.Add((pending, null, refused
                ? new Http2StreamRefusedException($"stream {header.StreamId} refused; connection is retiring")
                : new Http2ClientException($"stream {header.StreamId} reset (error {error})")));
        }
    }

    private void HandleGoAway(ReadOnlySpan<byte> payload)
    {
        _retiring = true;

        // Everything above last_stream_id was never processed, so RFC 9113 8.7 makes resending it
        // safe whatever the method. Below it the origin may have acted on the request, so those
        // streams are left in flight to finish or fail on their own.
        int lastStreamId = payload.Length >= 4
            ? (int)(BinaryPrimitives.ReadUInt32BigEndian(payload) & 0x7FFFFFFF)
            : 0;

        int[] refused = [.. _pending.Keys.Where(id => id > lastStreamId)];
        foreach (int streamId in refused)
        {
            if (_pending.Remove(streamId, out PendingRequest? pending))
            {
                _completedThisPass.Add((pending, null, new Http2StreamRefusedException(
                    $"stream {streamId} is past the GOAWAY last-stream-id; request was not processed")));
            }
        }
    }

    // --- completion -----------------------------------------------------------------------------

    // Runs OUTSIDE the parse, so a resumed caller may safely submit again - or retry through the
    // pool, which may dispose this very connection.
    private void CompleteFinishedRequests()
    {
        if (_completedThisPass.Count == 0)
        {
            return;
        }

        // Snapshot-and-clear first: a resumed caller can land new completions here, and the list
        // must not be mutated while it is being walked.
        var finished = _completedThisPass.ToArray();
        _completedThisPass.Clear();

        foreach ((PendingRequest pending, HttpClientResponse? response, Exception? error) in finished)
        {
            if (error is not null)
            {
                pending.Dispose();
                pending.Fail(error);
                continue;
            }

            // The arena refused bytes for exceeding MaxResponseBytes. Parsing could only record
            // that; failing the caller happens here, where a throw no longer unwinds the parser.
            if (response!.Overflowed)
            {
                response.Dispose();
                pending.Dispose();
                pending.Fail(new Http2ClientException(
                    $"response exceeds MaxResponseBytes ({_maxResponseBytes})"));
                continue;
            }

            pending.Dispose();
            pending.Complete(response);
        }
    }

    private void FailAll(Exception error)
    {
        // Broken before anything resumes: an inline-resumed caller that immediately retries must
        // see IsBroken and go to the pool, not submit onto this connection.
        _failed = true;
        _ready.TrySetException(error);

        // Recorded but not yet flushed: those pendings already left _pending, so they would be
        // missed below. A recorded response is a real completed exchange - deliver it.
        CompleteFinishedRequests();

        if (_pending.Count == 0)
        {
            return;
        }

        // Snapshot-and-clear: resumes run inline and may re-enter (a retry submitting elsewhere
        // still touches pool state), and _pending must not change under the enumeration.
        PendingRequest[] pendings = [.. _pending.Values];
        _pending.Clear();
        foreach (PendingRequest pending in pendings)
        {
            pending.Dispose();
            pending.Fail(error);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _failed = true;

        _orphanBlock.Dispose();
        if (_inbound.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_inbound);
            _inbound = [];
        }
        _inboundUsed = 0;

        _transport.Dispose();
    }

    /// <summary>One in-flight request. IValueTaskSource with asynchronous continuations OFF, so the
    /// caller resumes inline on the reactor thread.</summary>
    private sealed class PendingRequest : IValueTaskSource<HttpClientResponse>, IDisposable
    {
        private ManualResetValueTaskSourceCore<HttpClientResponse> _core = new()
        {
            RunContinuationsAsynchronously = false,
        };

        private byte[] _block = [];
        private int _blockUsed;

        public int StreamId;
        public HttpClientResponse? Response;
        public ResponseAssembly Assembly;

        /// <summary>What the peer will still accept on this stream. Starts at its advertised default.</summary>
        public int SendWindow = 65535;

        /// <summary>Request body not yet on the wire, held back by flow control.</summary>
        public ReadOnlyMemory<byte> BodyRemaining;

        /// <summary>END_STREAM arrived on a HEADERS whose block is still being continued.</summary>
        public bool HeadersEndedStream;

        public ValueTask<HttpClientResponse> Task => new(this, _core.Version);

        public ReadOnlySpan<byte> HeaderBlock => _block.AsSpan(0, _blockUsed);

        // A header block can span HEADERS + CONTINUATION frames, and HPACK cannot be decoded
        // piecewise - the whole block has to be in hand first.
        public void AppendHeaderBlock(ReadOnlySpan<byte> data)
        {
            if (_block.Length - _blockUsed < data.Length)
            {
                byte[] grown = ArrayPool<byte>.Shared.Rent(Math.Max(4096, (_blockUsed + data.Length) * 2));
                _block.AsSpan(0, _blockUsed).CopyTo(grown);
                if (_block.Length > 0)
                {
                    ArrayPool<byte>.Shared.Return(_block);
                }
                _block = grown;
            }
            data.CopyTo(_block.AsSpan(_blockUsed));
            _blockUsed += data.Length;
        }

        public void ClearHeaderBlock()
        {
            if (_block.Length > 0)
            {
                ArrayPool<byte>.Shared.Return(_block);
                _block = [];
            }
            _blockUsed = 0;
        }

        public void Complete(HttpClientResponse response) => _core.SetResult(response);

        public void Fail(Exception error) => _core.SetException(error);

        public HttpClientResponse GetResult(short token) => _core.GetResult(token);

        public ValueTaskSourceStatus GetStatus(short token) => _core.GetStatus(token);

        public void OnCompleted(Action<object?> continuation, object? state, short token,
            ValueTaskSourceOnCompletedFlags flags)
            => _core.OnCompleted(continuation, state, token, flags & ~ValueTaskSourceOnCompletedFlags.UseSchedulingContext);

        public void Dispose() => ClearHeaderBlock();
    }
}

/// <summary>Any HTTP/2 client failure: connect, session, stream, or a protocol error.</summary>
public class Http2ClientException : Exception
{
    public Http2ClientException(string message) : base(message) { }
}

/// <summary>
/// The peer refused the stream (REFUSED_STREAM), meaning it never processed the request - so
/// resending it on a fresh connection is safe regardless of the method's idempotence.
/// </summary>
public sealed class Http2StreamRefusedException : Http2ClientException
{
    public Http2StreamRefusedException(string message) : base(message) { }
}
