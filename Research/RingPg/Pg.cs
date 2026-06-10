using System.Buffers.Binary;
using System.Text;

namespace RingPg;

/// <summary>Minimal Postgres v3 wire protocol (trust auth, simple Query). Pure — no reactor, no ring.</summary>
public static unsafe class Pg
{
    public static int Startup(byte* buf, string user, string db)
    {
        int pos = 8;                                  // reserve len(4) + protocol(4)
        pos += WriteCStr(buf, pos, "user");     pos += WriteCStr(buf, pos, user);
        pos += WriteCStr(buf, pos, "database"); pos += WriteCStr(buf, pos, db);
        buf[pos++] = 0;                               // params terminator
        BinaryPrimitives.WriteInt32BigEndian(new Span<byte>(buf, 4), pos);
        BinaryPrimitives.WriteInt32BigEndian(new Span<byte>(buf + 4, 4), 196608);  // protocol 3.0
        return pos;
    }

    public static int Query(byte* buf, string sql)
    {
        buf[0] = (byte)'Q';
        int s = WriteCStr(buf, 5, sql);
        BinaryPrimitives.WriteInt32BigEndian(new Span<byte>(buf + 1, 4), 4 + s);
        return 5 + s;
    }

    private static int WriteCStr(byte* buf, int pos, string s)
    {
        int n = Encoding.ASCII.GetBytes(s, new Span<byte>(buf + pos, 512));
        buf[pos + n] = 0;
        return n + 1;
    }

    /// <summary>Walk messages; capture the first DataRow's first field (start/len indices into
    /// <paramref name="recv"/>) and whether ReadyForQuery ('Z') was seen.</summary>
    public static bool TryParse(ReadOnlySpan<byte> recv, out int fieldStart, out int fieldLen, out bool ready)
    {
        fieldStart = -1; fieldLen = 0; ready = false;
        int pos = 0;
        while (pos + 5 <= recv.Length)
        {
            byte type = recv[pos];
            int len = BinaryPrimitives.ReadInt32BigEndian(recv[(pos + 1)..]);
            if (len < 4 || pos + 1 + len > recv.Length) break;   // incomplete message
            int payload = pos + 5;
            if (type == (byte)'D' && fieldStart < 0)             // DataRow
            {
                int fp = payload + 2;                             // skip int16 field count
                int flen = BinaryPrimitives.ReadInt32BigEndian(recv[fp..]); fp += 4;
                if (flen >= 0) { fieldStart = fp; fieldLen = flen; }
            }
            else if (type == (byte)'Z') ready = true;            // ReadyForQuery
            pos += 1 + len;
        }
        return ready;
    }
}
