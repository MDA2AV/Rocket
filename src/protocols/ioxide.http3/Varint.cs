namespace ioxide.http3;

/// <summary>
/// QUIC variable-length integers (RFC 9000 §16): the top two bits of the first byte select the
/// width (1/2/4/8 bytes), the rest is the big-endian value. HTTP/3 frames and stream types are
/// built out of these.
/// </summary>
internal static class Varint
{
    /// <summary>Parse one varint; false when the span doesn't yet hold the whole encoding.</summary>
    public static bool TryRead(ReadOnlySpan<byte> input, out long value, out int consumed)
    {
        value = 0;
        consumed = 0;
        if (input.IsEmpty)
        {
            return false;
        }

        int len = 1 << (input[0] >> 6);
        if (input.Length < len)
        {
            return false;
        }

        long v = input[0] & 0x3F;
        for (int i = 1; i < len; i++)
        {
            v = (v << 8) | input[i];
        }
        value = v;
        consumed = len;
        return true;
    }

    /// <summary>Write a varint in its smallest encoding; returns bytes written.</summary>
    public static int Write(Span<byte> output, long value)
    {
        if (value < 1L << 6)
        {
            output[0] = (byte)value;
            return 1;
        }
        if (value < 1L << 14)
        {
            output[0] = (byte)(0x40 | (value >> 8));
            output[1] = (byte)value;
            return 2;
        }
        if (value < 1L << 30)
        {
            output[0] = (byte)(0x80 | (value >> 24));
            output[1] = (byte)(value >> 16);
            output[2] = (byte)(value >> 8);
            output[3] = (byte)value;
            return 4;
        }
        output[0] = (byte)(0xC0 | (value >> 56));
        for (int i = 1; i < 8; i++)
        {
            output[i] = (byte)(value >> (8 * (7 - i)));
        }
        return 8;
    }
}
