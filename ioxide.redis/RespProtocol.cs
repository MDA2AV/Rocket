using System.Buffers.Text;

namespace ioxide.redis;

/// <summary>RESP2 wire format: write a command as an array of bulk strings; parse one reply.</summary>
internal static class RespProtocol
{
    /// <summary>Bytes needed to encode a command (name + args) as a RESP array of bulk strings.</summary>
    public static int CommandSize(ReadOnlySpan<byte> name, ReadOnlySpan<RedisArg> args)
    {
        int size = 1 + IntLen(args.Length + 1) + 2;       // *<n>\r\n
        size += BulkSize(name.Length);
        foreach (RedisArg arg in args)
        {
            size += BulkSize(arg.Bytes.Length);
        }
        return size;
    }

    /// <summary>Write a command into <paramref name="dst"/> (sized by <see cref="CommandSize"/>).</summary>
    public static int WriteCommand(Span<byte> dst, ReadOnlySpan<byte> name, ReadOnlySpan<RedisArg> args)
    {
        int p = 0;
        dst[p++] = (byte)'*';
        p += WriteInt(dst[p..], args.Length + 1);
        dst[p++] = (byte)'\r';
        dst[p++] = (byte)'\n';

        p += WriteBulk(dst[p..], name);
        foreach (RedisArg arg in args)
        {
            p += WriteBulk(dst[p..], arg.Bytes);
        }
        return p;
    }

    private static int WriteBulk(Span<byte> dst, ReadOnlySpan<byte> payload)
    {
        int p = 0;
        dst[p++] = (byte)'$';
        p += WriteInt(dst[p..], payload.Length);
        dst[p++] = (byte)'\r';
        dst[p++] = (byte)'\n';
        payload.CopyTo(dst[p..]);
        p += payload.Length;
        dst[p++] = (byte)'\r';
        dst[p++] = (byte)'\n';
        return p;
    }

    private static int BulkSize(int payloadLength) => 1 + IntLen(payloadLength) + 2 + payloadLength + 2;

    private static int WriteInt(Span<byte> dst, int value)
    {
        Utf8Formatter.TryFormat(value, dst, out int written);
        return written;
    }

    private static int IntLen(int value)
    {
        int len = value < 0 ? 1 : 0;
        do { len++; value /= 10; } while (value != 0);
        return len;
    }

    /// <summary>
    /// Parse one complete reply (recursively, for arrays) from <paramref name="buffer"/>. False if
    /// the reply isn't fully buffered yet; on true, <paramref name="consumed"/> is its byte length.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> buffer, out RespValue value, out int consumed)
    {
        int pos = 0;
        if (TryParseAt(buffer, ref pos, out value!))
        {
            consumed = pos;
            return true;
        }
        consumed = 0;
        return false;
    }

    private static bool TryParseAt(ReadOnlySpan<byte> buffer, ref int pos, out RespValue? value)
    {
        value = null;
        if (pos >= buffer.Length)
        {
            return false;
        }

        byte type = buffer[pos];
        int lineEnd = buffer[(pos + 1)..].IndexOf("\r\n"u8);
        if (lineEnd < 0)
        {
            return false;
        }
        ReadOnlySpan<byte> line = buffer.Slice(pos + 1, lineEnd);
        int afterLine = pos + 1 + lineEnd + 2;

        switch (type)
        {
            case (byte)'+':
                value = RespValue.Simple(line.ToArray());
                pos = afterLine;
                return true;

            case (byte)'-':
                value = RespValue.Err(line.ToArray());
                pos = afterLine;
                return true;

            case (byte)':':
                value = RespValue.Int(ParseLong(line));
                pos = afterLine;
                return true;

            case (byte)'$':
            {
                long len = ParseLong(line);
                if (len == -1)
                {
                    value = RespValue.Null;
                    pos = afterLine;
                    return true;
                }
                if (afterLine + len + 2 > buffer.Length)
                {
                    return false;
                }
                value = RespValue.Bulk(buffer.Slice(afterLine, (int)len).ToArray());
                pos = afterLine + (int)len + 2;
                return true;
            }

            case (byte)'*':
            {
                long count = ParseLong(line);
                if (count == -1)
                {
                    value = RespValue.Null;
                    pos = afterLine;
                    return true;
                }
                var items = new RespValue[count];
                int p = afterLine;
                for (int i = 0; i < count; i++)
                {
                    if (!TryParseAt(buffer, ref p, out RespValue? item))
                    {
                        return false;
                    }
                    items[i] = item!;
                }
                value = RespValue.Arr(items);
                pos = p;
                return true;
            }

            default:
                throw new RedisException($"unexpected RESP type byte 0x{type:x2}");
        }
    }

    private static long ParseLong(ReadOnlySpan<byte> s)
    {
        bool neg = s.Length > 0 && s[0] == (byte)'-';
        if (neg) s = s[1..];
        long n = 0;
        foreach (byte c in s)
        {
            n = n * 10 + (c - '0');
        }
        return neg ? -n : n;
    }
}
