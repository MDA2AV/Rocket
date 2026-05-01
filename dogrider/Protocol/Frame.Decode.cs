using System.Buffers;

namespace dogrider.Protocol;

public static partial class Frame
{
    private const string IncompleteFrame = "Incomplete frame";
    private const string InvalidOpCode = "Invalid OpCode";
    private const string InvalidControlFrame = "Invalid Control Frame";
    private const string InvalidControlFrameLength = "Invalid Control Frame Length";
    private const string PayloadTooLarge = "Payload is too large";

    /// <summary>
    /// RFC 6455 frame decode. Parses one frame out of <paramref name="sequence"/>; sets
    /// <paramref name="consumed"/>/<paramref name="examined"/> per System.IO.Pipelines convention.
    /// On <see cref="FrameErrorType.Incomplete"/> the caller should obtain more bytes and retry
    /// (positions are reset to sequence.Start/End so no progress is lost).
    /// </summary>
    /// <remarks>
    /// Unmask is performed in-place against the underlying buffer of each segment via Memory.Pin().
    /// That works for kernel io_uring buffers (UnmanagedMemoryManager) and managed arrays alike,
    /// so the same decoder runs against zerg rings and ordinary byte[] (e.g. carryover buffers).
    /// </remarks>
    public static WebsocketFrame Decode(
        ReadOnlySequence<byte> sequence,
        int rxMaxBufferSize,
        out SequencePosition consumed,
        out SequencePosition examined)
    {
        var reader = new SequenceReader<byte>(sequence);

        consumed = sequence.Start;
        examined = sequence.End;

        if (reader.Remaining < 2)
            return Incomplete(sequence, out consumed, out examined);

        reader.TryRead(out byte b0);
        reader.TryRead(out byte b1);

        var fin = (b0 & 0b1000_0000) != 0;
        var opcode = (byte)(b0 & 0x0F);

        var frameType = opcode switch
        {
            0x00 => FrameType.Continue,
            0x01 => FrameType.Text,
            0x02 => FrameType.Binary,
            0x08 => FrameType.Close,
            0x09 => FrameType.Ping,
            0x0A => FrameType.Pong,
            _    => FrameType.None
        };

        if (frameType == FrameType.None)
        {
            consumed = reader.Position;
            examined = reader.Position;
            return new WebsocketFrame(new FrameError(InvalidOpCode, FrameErrorType.InvalidOpCode));
        }

        var isControlFrame = frameType is FrameType.Close or FrameType.Ping or FrameType.Pong;
        var isMasked = (b1 & 0x80) != 0;
        var payloadLen7 = (byte)(b1 & 0x7F);

        if (isControlFrame && !fin)
        {
            consumed = reader.Position;
            examined = reader.Position;
            return new WebsocketFrame(new FrameError(InvalidControlFrame, FrameErrorType.InvalidControlFrame));
        }

        if (isControlFrame && payloadLen7 >= 126)
        {
            consumed = reader.Position;
            examined = reader.Position;
            return new WebsocketFrame(new FrameError(InvalidControlFrameLength, FrameErrorType.InvalidControlFrameLength));
        }

        long payloadLen64 = payloadLen7;

        if (payloadLen7 == 126)
        {
            if (reader.Remaining < 2)
                return Incomplete(sequence, out consumed, out examined);

            reader.TryReadBigEndian(out short len16);
            payloadLen64 = (ushort)len16;
        }
        else if (payloadLen7 == 127)
        {
            if (reader.Remaining < 8)
                return Incomplete(sequence, out consumed, out examined);

            reader.TryReadBigEndian(out long len64);
            payloadLen64 = len64;
        }

        const int MaxFrameHeaderSize = 14;
        var maxAllowedPayload = rxMaxBufferSize - MaxFrameHeaderSize;

        if (payloadLen64 < 0 || payloadLen64 > maxAllowedPayload || payloadLen64 > int.MaxValue)
        {
            consumed = reader.Position;
            examined = reader.Position;
            return new WebsocketFrame(new FrameError(PayloadTooLarge, FrameErrorType.PayloadTooLarge));
        }

        var payloadLength = (int)payloadLen64;

        Span<byte> maskKey = stackalloc byte[4];
        if (isMasked)
        {
            if (reader.Remaining < 4 || !reader.TryCopyTo(maskKey))
                return Incomplete(sequence, out consumed, out examined);

            reader.Advance(4);
        }

        if (reader.Remaining < payloadLength)
            return Incomplete(sequence, out consumed, out examined);

        var payloadStart = reader.Position;
        var payloadEnd = sequence.GetPosition(payloadLength, payloadStart);
        var payloadSeq = sequence.Slice(payloadStart, payloadEnd);

        if (isMasked && payloadLength != 0)
            UnmaskInPlace(payloadSeq, maskKey);

        reader.Advance(payloadLength);
        consumed = reader.Position;
        examined = reader.Position;

        return new WebsocketFrame(frameType, payloadSeq, fin);
    }

    private static WebsocketFrame Incomplete(in ReadOnlySequence<byte> seq, out SequencePosition c, out SequencePosition e)
    {
        c = seq.Start;
        e = seq.End;
        return new WebsocketFrame(new FrameError(IncompleteFrame, FrameErrorType.Incomplete));
    }

    private static unsafe void UnmaskInPlace(in ReadOnlySequence<byte> payload, ReadOnlySpan<byte> maskKey)
    {
        var maskOffset = 0;
        foreach (var mem in payload)
        {
            if (mem.IsEmpty) continue;

            using var pin = mem.Pin();
            var writable = new Span<byte>(pin.Pointer, mem.Length);

            for (var i = 0; i < writable.Length; i++)
                writable[i] ^= maskKey[(maskOffset + i) & 3];

            maskOffset = (maskOffset + writable.Length) & 3;
        }
    }
}
