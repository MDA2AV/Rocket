using System.Buffers.Binary;

namespace ioxide.http2;

/// <summary>HTTP/2 frame types, RFC 9113 section 6.</summary>
internal enum FrameType : byte
{
    Data         = 0x0,
    Headers      = 0x1,
    Priority     = 0x2,
    RstStream    = 0x3,
    Settings     = 0x4,
    PushPromise  = 0x5,
    Ping         = 0x6,
    GoAway       = 0x7,
    WindowUpdate = 0x8,
    Continuation = 0x9,
}

[Flags]
internal enum FrameFlags : byte
{
    None       = 0x00,
    EndStream  = 0x01,
    Ack        = 0x01,   // same bit as EndStream, but only on SETTINGS and PING
    EndHeaders = 0x04,
    Padded     = 0x08,
    Priority   = 0x20,
}

/// <summary>Error codes, RFC 9113 section 7.</summary>
internal static class Http2Error
{
    internal const uint NoError            = 0x0;
    internal const uint ProtocolError      = 0x1;
    internal const uint InternalError      = 0x2;
    internal const uint FlowControlError   = 0x3;
    internal const uint SettingsTimeout    = 0x4;
    internal const uint StreamClosed       = 0x5;
    internal const uint FrameSizeError     = 0x6;
    internal const uint RefusedStream      = 0x7;
    internal const uint Cancel             = 0x8;
    internal const uint CompressionError   = 0x9;
    internal const uint EnhanceYourCalm    = 0xb;
}

/// <summary>
/// The nine-byte frame header every HTTP/2 frame carries: a 24-bit length, a type, flags, and a
/// 31-bit stream id with one reserved bit above it.
/// </summary>
internal readonly record struct FrameHeader(int Length, FrameType Type, FrameFlags Flags, int StreamId)
{
    internal const int Size = 9;

    /// <summary>The connection preface a client sends before anything else, RFC 9113 section 3.4.</summary>
    internal static ReadOnlySpan<byte> ClientPreface => "PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"u8;

    internal static bool TryRead(ReadOnlySpan<byte> source, out FrameHeader header)
    {
        if (source.Length < Size)
        {
            header = default;
            return false;
        }

        int length = (source[0] << 16) | (source[1] << 8) | source[2];

        // The reserved high bit is masked rather than rejected: RFC 9113 says receivers must
        // ignore it, and some peers do set it.
        int streamId = (int)(BinaryPrimitives.ReadUInt32BigEndian(source[5..]) & 0x7FFFFFFF);

        header = new FrameHeader(length, (FrameType)source[3], (FrameFlags)source[4], streamId);
        return true;
    }

    internal void Write(Span<byte> destination)
    {
        destination[0] = (byte)((Length >> 16) & 0xFF);
        destination[1] = (byte)((Length >> 8) & 0xFF);
        destination[2] = (byte)(Length & 0xFF);
        destination[3] = (byte)Type;
        destination[4] = (byte)Flags;
        BinaryPrimitives.WriteUInt32BigEndian(destination[5..], (uint)StreamId);
    }
}

/// <summary>SETTINGS identifiers, RFC 9113 section 6.5.2.</summary>
internal static class Http2Setting
{
    internal const ushort HeaderTableSize      = 0x1;
    internal const ushort EnablePush           = 0x2;
    internal const ushort MaxConcurrentStreams = 0x3;
    internal const ushort InitialWindowSize    = 0x4;
    internal const ushort MaxFrameSize         = 0x5;
    internal const ushort MaxHeaderListSize    = 0x6;
}
