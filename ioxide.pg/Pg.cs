using System.Buffers.Binary;
using System.Text;

namespace ioxide.pg;

/// <summary>
/// The Postgres v3 frontend/backend protocol — just enough of it for trust authentication and a
/// simple text query. This class is pure: it formats and parses bytes, and knows nothing about
/// io_uring, reactors, or sockets.
/// </summary>
internal static unsafe class Pg
{
    // The protocol version is 3.0, encoded as the major number in the high 16 bits.
    private const int ProtocolVersion3 = 3 << 16;

    /// <summary>
    /// Write a StartupMessage carrying the user and database parameters into
    /// <paramref name="buffer"/>, and return its total length. This is the first thing a client
    /// sends after the socket connects.
    /// </summary>
    public static int Startup(byte* buffer, string user, string database)
    {
        // The first eight bytes are the message length and the protocol version. We don't know the
        // length until the parameter list is written, so leave room and fill them in at the end.
        int position = 8;

        position += WriteCString(buffer, position, "user");
        position += WriteCString(buffer, position, user);

        position += WriteCString(buffer, position, "database");
        position += WriteCString(buffer, position, database);

        // An empty key (a lone NUL) terminates the parameter list.
        buffer[position++] = 0;

        BinaryPrimitives.WriteInt32BigEndian(new Span<byte>(buffer, 4), position);
        BinaryPrimitives.WriteInt32BigEndian(new Span<byte>(buffer + 4, 4), ProtocolVersion3);

        return position;
    }

    /// <summary>
    /// Write a simple Query message ('Q') wrapping <paramref name="sql"/> into
    /// <paramref name="buffer"/>, and return its total length.
    /// </summary>
    public static int Query(byte* buffer, string sql)
    {
        // Layout: a 'Q' tag, a 4-byte length covering everything after the tag, then the SQL text
        // as a NUL-terminated string.
        buffer[0] = (byte)'Q';

        int bodyLength = WriteCString(buffer, 5, sql);

        BinaryPrimitives.WriteInt32BigEndian(new Span<byte>(buffer + 1, 4), 4 + bodyLength);

        return 5 + bodyLength;
    }

    private static int WriteCString(byte* buffer, int position, string text)
    {
        int written = Encoding.ASCII.GetBytes(text, new Span<byte>(buffer + position, 512));

        buffer[position + written] = 0;

        return written + 1;
    }

    /// <summary>
    /// Walk the complete messages currently in <paramref name="received"/>. Report the first
    /// DataRow's first field (as start and length indices into the buffer), and return whether a
    /// ReadyForQuery message has arrived — which is how the client knows the response is finished.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> received, out int fieldStart, out int fieldLength)
    {
        fieldStart = -1;
        fieldLength = 0;

        bool ready = false;
        int position = 0;

        // Every message is a 1-byte tag, a 4-byte length (which counts itself), then the body.
        while (position + 5 <= received.Length)
        {
            byte tag = received[position];
            int length = BinaryPrimitives.ReadInt32BigEndian(received[(position + 1)..]);

            // The message hasn't fully arrived yet. Stop and wait for the next recv.
            if (length < 4 || position + 1 + length > received.Length)
                break;

            int body = position + 5;

            if (tag == (byte)'D' && fieldStart < 0)
            {
                // DataRow: an int16 field count, then for each field an int32 length and its bytes.
                // A length of -1 means SQL NULL; we only capture the first non-null field.
                int cursor = body + 2;

                int valueLength = BinaryPrimitives.ReadInt32BigEndian(received[cursor..]);
                cursor += 4;

                if (valueLength >= 0)
                {
                    fieldStart = cursor;
                    fieldLength = valueLength;
                }
            }
            else if (tag == (byte)'Z')
            {
                ready = true;
            }

            position += 1 + length;
        }

        return ready;
    }
}
