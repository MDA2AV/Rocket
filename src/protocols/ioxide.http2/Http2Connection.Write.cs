using System.Buffers;
using System.Buffers.Binary;

namespace ioxide.http2;

/// <summary>
/// The write side: everything is staged into the connection's write slab and flushed once per pass,
/// so a batch of multiplexed responses leaves in one send rather than one each.
/// </summary>
public sealed partial class Http2Connection
{
    private bool _staged;

    private void Stage(ReadOnlySpan<byte> bytes)
    {
        _connection.Write(bytes);
        _staged = true;
    }

    private async ValueTask FlushAsync()
    {
        if (!_staged)
        {
            return;
        }
        _staged = false;
        await _connection.FlushAsync();
    }

    private void WriteSettings()
    {
        Span<byte> frame = stackalloc byte[FrameHeader.Size + (6 * 4)];
        new FrameHeader(6 * 4, FrameType.Settings, FrameFlags.None, 0).Write(frame);

        Span<byte> body = frame[FrameHeader.Size..];
        WriteSetting(body, 0, Http2Setting.EnablePush, 0);                                   // nothing routes a push
        WriteSetting(body, 1, Http2Setting.MaxConcurrentStreams, (uint)_options.MaxConcurrentStreams);
        WriteSetting(body, 2, Http2Setting.InitialWindowSize, (uint)_options.InitialWindowSize);
        WriteSetting(body, 3, Http2Setting.MaxFrameSize, (uint)_options.MaxFrameSize);

        Stage(frame);
    }

    private static void WriteSetting(Span<byte> body, int slot, ushort id, uint value)
    {
        BinaryPrimitives.WriteUInt16BigEndian(body[(slot * 6)..], id);
        BinaryPrimitives.WriteUInt32BigEndian(body[((slot * 6) + 2)..], value);
    }

    private void WriteSettingsAck()
    {
        Span<byte> frame = stackalloc byte[FrameHeader.Size];
        new FrameHeader(0, FrameType.Settings, FrameFlags.Ack, 0).Write(frame);
        Stage(frame);
    }

    private void WriteWindowUpdate(int streamId, int increment)
    {
        Span<byte> frame = stackalloc byte[FrameHeader.Size + 4];
        new FrameHeader(4, FrameType.WindowUpdate, FrameFlags.None, streamId).Write(frame);
        BinaryPrimitives.WriteUInt32BigEndian(frame[FrameHeader.Size..], (uint)increment);
        Stage(frame);
    }

    private void ResetStream(int streamId, uint errorCode)
    {
        if (_streams.Remove(streamId, out PendingRequest? pending))
        {
            pending.Dispose();
        }

        Span<byte> frame = stackalloc byte[FrameHeader.Size + 4];
        new FrameHeader(4, FrameType.RstStream, FrameFlags.None, streamId).Write(frame);
        BinaryPrimitives.WriteUInt32BigEndian(frame[FrameHeader.Size..], errorCode);
        Stage(frame);
    }

    private void GoAway(uint errorCode)
    {
        Span<byte> frame = stackalloc byte[FrameHeader.Size + 8];
        new FrameHeader(8, FrameType.GoAway, FrameFlags.None, 0).Write(frame);
        BinaryPrimitives.WriteUInt32BigEndian(frame[FrameHeader.Size..], 0);       // last stream id
        BinaryPrimitives.WriteUInt32BigEndian(frame[(FrameHeader.Size + 4)..], errorCode);
        Stage(frame);
        _failed = true;
    }

    private static readonly byte[] StatusName = ":status"u8.ToArray();

    private void WriteResponse(int streamId, Http2Response response)
    {
        WriteHeaders(streamId, response, endStream: response.Body.Length == 0);

        if (response.Body.Length > 0)
        {
            WriteData(streamId, response.Body.Span);
        }
    }

    private void WriteHeaders(int streamId, Http2Response response, bool endStream)
    {
        int capacity = HpackEncoder.MaxEncodedLength(StatusName.Length, 3);
        foreach (KeyValuePair<ReadOnlyMemory<byte>, ReadOnlyMemory<byte>> field in response.Headers.AsSpan())
        {
            capacity += HpackEncoder.MaxEncodedLength(field.Key.Length, field.Value.Length);
        }

        byte[] block = ArrayPool<byte>.Shared.Rent(capacity);
        try
        {
            int written = 0;

            Span<byte> status = stackalloc byte[3];
            WriteStatus(response.Status, status);
            written += HpackEncoder.Encode(block.AsSpan(written), StatusName, status);

            foreach (KeyValuePair<ReadOnlyMemory<byte>, ReadOnlyMemory<byte>> field in response.Headers.AsSpan())
            {
                written += HpackEncoder.Encode(block.AsSpan(written), field.Key.Span, field.Value.Span);
            }

            // A header block over MAX_FRAME_SIZE must continue in CONTINUATION frames; nothing else
            // may interleave on the connection between them.
            int offset = 0;
            bool first = true;

            while (offset < written || first)
            {
                int chunk = Math.Min(_peerMaxFrameSize, written - offset);
                bool last = offset + chunk >= written;

                FrameFlags flags = FrameFlags.None;
                if (last)
                {
                    flags |= FrameFlags.EndHeaders;
                    if (endStream)
                    {
                        flags |= FrameFlags.EndStream;
                    }
                }

                Span<byte> header = stackalloc byte[FrameHeader.Size];
                new FrameHeader(chunk, first ? FrameType.Headers : FrameType.Continuation, flags, streamId)
                    .Write(header);

                Stage(header);
                if (chunk > 0)
                {
                    Stage(block.AsSpan(offset, chunk));
                }

                offset += chunk;
                first = false;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(block);
        }
    }

    private void WriteData(int streamId, ReadOnlySpan<byte> body)
    {
        int offset = 0;

        while (offset < body.Length)
        {
            // Bounded by the frame size AND by both flow-control windows: exceeding either is a
            // connection error, and the peer would be right to hang up.
            int chunk = Math.Min(_peerMaxFrameSize, body.Length - offset);
            chunk = Math.Min(chunk, _peerConnectionWindow);

            if (chunk <= 0)
            {
                // Out of credit. The rest is dropped rather than queued: this server buffers whole
                // responses, and a 1 MiB default window makes running out mean the peer has stopped
                // reading entirely. Streaming responses would need a send queue here instead.
                ResetStream(streamId, Http2Error.FlowControlError);
                return;
            }

            bool last = offset + chunk >= body.Length;

            Span<byte> header = stackalloc byte[FrameHeader.Size];
            new FrameHeader(chunk, FrameType.Data, last ? FrameFlags.EndStream : FrameFlags.None, streamId)
                .Write(header);

            Stage(header);
            Stage(body.Slice(offset, chunk));

            _peerConnectionWindow -= chunk;
            offset += chunk;
        }
    }

    // Three ASCII digits: HTTP/2 has no reason phrase and every valid status is 100..599.
    private static void WriteStatus(int status, Span<byte> destination)
    {
        int clamped = status is >= 100 and <= 599 ? status : 500;
        destination[0] = (byte)('0' + (clamped / 100));
        destination[1] = (byte)('0' + ((clamped / 10) % 10));
        destination[2] = (byte)(clamped % 10 + '0');
    }
}
