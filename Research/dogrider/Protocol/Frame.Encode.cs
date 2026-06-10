using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using zerg.core;

namespace dogrider.Protocol;

public static partial class Frame
{
    private const int MaxSmallPayloadLength = 125;

    // Server-to-client frames are unmasked per RFC 6455.

    /// <summary>
    /// Encodes a frame directly into the connection's write buffer (no ArrayPool, no IMemoryOwner).
    /// Caller is responsible for invoking <c>FlushAsync</c> when ready to send.
    /// </summary>
    public static void EncodeInto(ConnectionBase conn, ReadOnlySpan<byte> payload, byte opcode, bool fin = true)
    {
        ValidateOpcodeFin(opcode, fin);

        var headerLen = HeaderLength(payload.Length);
        var totalLen = headerLen + payload.Length;

        var dest = conn.GetSpan(totalLen);
        WriteHeader(dest, payload.Length, opcode, fin);
        payload.CopyTo(dest.Slice(headerLen));
        conn.Advance(totalLen);
    }

    public static void EncodeText(ConnectionBase conn, ReadOnlySpan<byte> utf8Payload, bool fin = true)
        => EncodeInto(conn, utf8Payload, opcode: 0x01, fin);

    public static void EncodeText(ConnectionBase conn, string text, bool fin = true)
    {
        var byteCount = Encoding.UTF8.GetByteCount(text);
        var headerLen = HeaderLength(byteCount);
        var totalLen = headerLen + byteCount;

        var dest = conn.GetSpan(totalLen);
        WriteHeader(dest, byteCount, opcode: 0x01, fin);
        Encoding.UTF8.GetBytes(text, dest.Slice(headerLen));
        conn.Advance(totalLen);
    }

    public static void EncodeBinary(ConnectionBase conn, ReadOnlySpan<byte> payload, bool fin = true)
        => EncodeInto(conn, payload, opcode: 0x02, fin);

    public static void EncodePing(ConnectionBase conn, ReadOnlySpan<byte> payload = default)
        => EncodeInto(conn, payload, opcode: 0x09, fin: true);

    public static void EncodePong(ConnectionBase conn, ReadOnlySpan<byte> payload)
        => EncodeInto(conn, payload, opcode: 0x0A, fin: true);

    public static void EncodeClose(ConnectionBase conn, ushort statusCode, string? reason = null)
    {
        if (string.IsNullOrEmpty(reason))
        {
            Span<byte> small = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(small, statusCode);
            EncodeInto(conn, small, opcode: 0x08, fin: true);
            return;
        }

        var reasonByteCount = Encoding.UTF8.GetByteCount(reason);
        var totalPayload = 2 + reasonByteCount;

        if (totalPayload > MaxSmallPayloadLength)
            throw new ArgumentException("Close reason too long (must fit in 125 bytes including the 2-byte status code).", nameof(reason));

        var rented = ArrayPool<byte>.Shared.Rent(totalPayload);
        try
        {
            var span = rented.AsSpan(0, totalPayload);
            BinaryPrimitives.WriteUInt16BigEndian(span[..2], statusCode);
            Encoding.UTF8.GetBytes(reason, span[2..]);
            EncodeInto(conn, span, opcode: 0x08, fin: true);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static int HeaderLength(int payloadLen) => payloadLen switch
    {
        <= MaxSmallPayloadLength => 2,
        <= ushort.MaxValue       => 4,
        _                        => 10,
    };

    private static void WriteHeader(Span<byte> dest, int payloadLength, byte opcode, bool fin)
    {
        var finBit = fin ? (byte)0x80 : (byte)0x00;
        dest[0] = (byte)(finBit | opcode);

        switch (payloadLength)
        {
            case <= MaxSmallPayloadLength:
                dest[1] = (byte)payloadLength;
                break;
            case <= ushort.MaxValue:
                dest[1] = 126;
                BinaryPrimitives.WriteUInt16BigEndian(dest.Slice(2, 2), (ushort)payloadLength);
                break;
            default:
                dest[1] = 127;
                BinaryPrimitives.WriteUInt64BigEndian(dest.Slice(2, 8), (ulong)payloadLength);
                break;
        }
    }

    private static void ValidateOpcodeFin(byte opcode, bool fin)
    {
        if (opcode != 0x00 && opcode != 0x01 && opcode != 0x02 && opcode < 0x08)
            throw new ArgumentException("Invalid opcode.", nameof(opcode));
        if (opcode >= 0x08 && !fin)
            throw new ArgumentException("Control frames must not be fragmented (FIN=true required).", nameof(fin));
    }
}
