using System.Buffers;

namespace ioxide.http3;

/// <summary>
/// QPACK (RFC 9204) without the dynamic table: our SETTINGS advertise capacity 0, so a conforming
/// peer may only send static-table references and literals - which reduces the decoder to the
/// static table, prefixed integers, and Huffman. The encoder mirrors that: indexed :status where
/// the table has the code, literal name references otherwise, literal names for everything else,
/// no Huffman on output (legal, and keeps the writer trivial).
/// </summary>
internal static class Qpack
{
    // --- prefixed integers (RFC 7541 §5.1, reused by QPACK) ------------------------------------

    public static bool TryReadInt(ReadOnlySpan<byte> input, int prefixBits, out long value, out int consumed)
    {
        value = 0;
        consumed = 0;
        if (input.IsEmpty)
        {
            return false;
        }

        int max = (1 << prefixBits) - 1;
        long v = input[0] & max;
        consumed = 1;
        if (v < max)
        {
            value = v;
            return true;
        }

        int shift = 0;
        while (true)
        {
            if (consumed >= input.Length)
            {
                consumed = 0;
                return false;
            }
            byte b = input[consumed++];
            v += (long)(b & 0x7F) << shift;
            shift += 7;
            if ((b & 0x80) == 0)
            {
                value = v;
                return true;
            }
            if (shift > 56)
            {
                consumed = 0;
                return false;   // absurd - treat as malformed
            }
        }
    }

    public static int WriteInt(Span<byte> output, byte firstByteBits, int prefixBits, long value)
    {
        int max = (1 << prefixBits) - 1;
        if (value < max)
        {
            output[0] = (byte)(firstByteBits | value);
            return 1;
        }
        output[0] = (byte)(firstByteBits | max);
        value -= max;
        int w = 1;
        while (value >= 0x80)
        {
            output[w++] = (byte)(0x80 | (value & 0x7F));
            value >>= 7;
        }
        output[w++] = (byte)value;
        return w;
    }

    // --- field-section decoding ----------------------------------------------------------------

    /// <summary>
    /// Decode a whole encoded field section into <paramref name="request"/> (pseudo-headers routed
    /// to the dedicated fields, the rest appended). Returns false on malformed input or any
    /// dynamic-table reference (illegal against our capacity-0 advertisement).
    /// </summary>
    public static bool TryDecodeFieldSection(ReadOnlySpan<byte> input, Http3Request request)
    {
        // Prefix: Required Insert Count (8-bit prefix) + sign/Delta Base (7-bit prefix). With no
        // dynamic table a conforming encoder sends RIC = 0; anything else references state we
        // don't have.
        if (!TryReadInt(input, 8, out long ric, out int c1) || ric != 0)
        {
            return false;
        }
        input = input[c1..];
        if (!TryReadInt(input, 7, out _, out int c2))
        {
            return false;
        }
        input = input[c2..];

        while (!input.IsEmpty)
        {
            byte first = input[0];

            if ((first & 0x80) != 0)
            {
                // Indexed Field Line: 1 T index(6+). T=1 static.
                if ((first & 0x40) == 0)
                {
                    return false;   // dynamic reference
                }
                if (!TryReadInt(input, 6, out long idx, out int consumed) || idx >= QpackStatic.Table.Length)
                {
                    return false;
                }
                input = input[consumed..];
                (byte[] name, byte[] value) = QpackStatic.Table[idx];
                request.AddField(name, value);
                continue;
            }

            if ((first & 0x40) != 0)
            {
                // Literal With Name Reference: 01 N T index(4+), then value string.
                if ((first & 0x10) == 0)
                {
                    return false;   // dynamic name reference
                }
                if (!TryReadInt(input, 4, out long idx, out int consumed) || idx >= QpackStatic.Table.Length)
                {
                    return false;
                }
                input = input[consumed..];
                if (!TryDecodeString(ref input, 7, out byte[] value, out int valueLen))
                {
                    return false;
                }
                request.AddField(QpackStatic.Table[idx].Name, value.AsSpan(0, valueLen));
                ArrayPool<byte>.Shared.Return(value);
                continue;
            }

            if ((first & 0x20) != 0)
            {
                // Literal With Literal Name: 001 N H namelen(3+), name, then value string.
                if (!TryDecodeString(ref input, 3, out byte[] name, out int nameLen))
                {
                    return false;
                }
                if (!TryDecodeString(ref input, 7, out byte[] value, out int valueLen))
                {
                    ArrayPool<byte>.Shared.Return(name);
                    return false;
                }
                request.AddField(name.AsSpan(0, nameLen), value.AsSpan(0, valueLen));
                ArrayPool<byte>.Shared.Return(name);
                ArrayPool<byte>.Shared.Return(value);
                continue;
            }

            return false;   // post-base forms (0001/0000) - dynamic table only
        }

        return true;
    }

    // A QPACK string: H bit ahead of an N-bit-prefix length, then bytes (Huffman-coded when H).
    // Output is a pooled buffer + length; the caller returns it.
    private static bool TryDecodeString(ref ReadOnlySpan<byte> input, int prefixBits, out byte[] buffer, out int length)
    {
        buffer = [];
        length = 0;
        if (input.IsEmpty)
        {
            return false;
        }

        bool huffman = (input[0] & (1 << prefixBits)) != 0;
        if (!TryReadInt(input, prefixBits, out long len, out int consumed) || len > 256 * 1024)
        {
            return false;
        }
        input = input[consumed..];
        if (input.Length < len)
        {
            return false;
        }

        ReadOnlySpan<byte> raw = input[..(int)len];
        input = input[(int)len..];

        if (!huffman)
        {
            buffer = ArrayPool<byte>.Shared.Rent((int)len);
            raw.CopyTo(buffer);
            length = (int)len;
            return true;
        }

        buffer = ArrayPool<byte>.Shared.Rent(Huffman.MaxDecodedLength((int)len));
        length = Huffman.Decode(raw, buffer);
        if (length < 0)
        {
            ArrayPool<byte>.Shared.Return(buffer);
            buffer = [];
            return false;
        }
        return true;
    }

    // --- field-section encoding (responses) ----------------------------------------------------

    private static readonly (int Status, byte Index)[] IndexedStatuses =
    [
        (103, 24), (200, 25), (304, 26), (404, 27), (503, 28),
        (100, 63), (204, 64), (206, 65), (302, 66), (400, 67), (403, 68), (421, 69), (425, 70), (500, 71),
    ];

    /// <summary>Encode a response's field section (prefix + :status + headers) into a pooled buffer.</summary>
    public static byte[] EncodeResponseFields(Http3Response response, out int written)
    {
        int cap = 2 + 8;
        foreach ((ReadOnlyMemory<byte> name, ReadOnlyMemory<byte> value) in response.Headers)
        {
            cap += 8 + name.Length + 8 + value.Length;
        }

        byte[] buf = ArrayPool<byte>.Shared.Rent(cap);
        int w = 0;

        // Prefix: RIC 0, Delta Base 0 - the no-dynamic-table constant.
        buf[w++] = 0x00;
        buf[w++] = 0x00;

        // :status - indexed when the static table has it, literal with name ref (idx 24) otherwise.
        byte statusIdx = 0;
        foreach ((int status, byte idx) in IndexedStatuses)
        {
            if (status == response.Status)
            {
                statusIdx = idx;
                break;
            }
        }
        if (statusIdx != 0)
        {
            w += WriteInt(buf.AsSpan(w), 0xC0, 6, statusIdx);        // 1 T=1 index
        }
        else
        {
            w += WriteInt(buf.AsSpan(w), 0x50, 4, 24);               // 01 N=0 T=1 nameidx
            Span<byte> digits = stackalloc byte[3];
            System.Buffers.Text.Utf8Formatter.TryFormat(response.Status, digits, out int dlen);
            w += WriteInt(buf.AsSpan(w), 0x00, 7, dlen);             // H=0 value
            digits[..dlen].CopyTo(buf.AsSpan(w));
            w += dlen;
        }

        foreach ((ReadOnlyMemory<byte> nameM, ReadOnlyMemory<byte> valueM) in response.Headers)
        {
            // Literal With Literal Name: 001 N=0 H=0 namelen(3+), lowercased name, H=0 value(7+).
            ReadOnlySpan<byte> name = nameM.Span;
            w += WriteInt(buf.AsSpan(w), 0x20, 3, name.Length);
            foreach (byte b in name)
            {
                buf[w++] = b is >= (byte)'A' and <= (byte)'Z' ? (byte)(b | 0x20) : b;
            }
            w += WriteInt(buf.AsSpan(w), 0x00, 7, valueM.Length);
            valueM.Span.CopyTo(buf.AsSpan(w));
            w += valueM.Length;
        }

        written = w;
        return buf;
    }
}
