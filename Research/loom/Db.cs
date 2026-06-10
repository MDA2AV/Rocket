using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks.Sources;
using static Loom.Native;

namespace Loom;

/// <summary>
/// A Postgres connection whose SEND/RECV ride loom's io_uring ring. The startup handshake is
/// done once with blocking syscalls; afterwards every query is submitted as IORING_OP_SEND /
/// IORING_OP_RECV on this fd, and the CQE completes the IVTS — so a DB await resumes inline on
/// the reactor, with NO .NET socket engine and NO thread pool.
/// </summary>
internal sealed unsafe class DbConn : IValueTaskSource<int>
{
    public const int Buf = 32 * 1024;

    public int Fd;
    public byte* Send;
    public byte* Recv;
    public int RecvLen;

    // IVTS for the in-flight ring SEND/RECV on the DB socket (RCA=false → inline resume).
    private ManualResetValueTaskSourceCore<int> _io = new() { RunContinuationsAsynchronously = false };

    public DbConn()
    {
        Send = (byte*)NativeMemory.Alloc(Buf);
        Recv = (byte*)NativeMemory.Alloc(Buf);
    }

    public ValueTask<int> IoAwait() { _io.Reset(); return new ValueTask<int>(this, _io.Version); }
    public void IoComplete(int res) => _io.SetResult(res);

    int IValueTaskSource<int>.GetResult(short t) => _io.GetResult(t);
    ValueTaskSourceStatus IValueTaskSource<int>.GetStatus(short t) => _io.GetStatus(t);
    void IValueTaskSource<int>.OnCompleted(Action<object?> c, object? s, short t, ValueTaskSourceOnCompletedFlags f)
        => _io.OnCompleted(c, s, t, f);
}

/// <summary>Minimal Postgres v3 wire protocol (trust auth, simple Query).</summary>
internal static unsafe class Pg
{
    [DllImport("libc", SetLastError = true)] private static extern int connect(int fd, sockaddr_in* addr, uint len);
    [DllImport("libc", SetLastError = true)] private static extern long send(int fd, void* buf, nuint n, int flags);
    [DllImport("libc", SetLastError = true)] private static extern long recv(int fd, void* buf, nuint n, int flags);

    /// <summary>Blocking connect + StartupMessage + read until ReadyForQuery. One-time per reactor.</summary>
    public static DbConn Connect(string user, string db)
    {
        var c = new DbConn();
        int fd = socket(AF_INET, SOCK_STREAM, 0);
        sockaddr_in addr = default;
        addr.sin_family = AF_INET;
        addr.sin_port = Htons(5432);
        addr.sin_addr.s_addr = 0x0100007F;          // 127.0.0.1 in network byte order
        if (connect(fd, &addr, (uint)sizeof(sockaddr_in)) < 0) throw new Exception("pg connect failed");
        c.Fd = fd;

        int len = Startup(c.Send, user, db);
        send(fd, c.Send, (nuint)len, 0);

        int rl = 0;
        while (true)
        {
            long n = recv(fd, c.Recv + rl, (nuint)(DbConn.Buf - rl), 0);
            if (n <= 0) throw new Exception("pg startup recv failed");
            rl += (int)n;
            if (TryParse(new ReadOnlySpan<byte>(c.Recv, rl), out _, out _, out bool ready) && ready) break;
        }
        return c;
    }

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

    /// <summary>Parse messages in <paramref name="recv"/>; capture the first DataRow's first field
    /// (start/len indices into recv) and whether ReadyForQuery ('Z') was seen.</summary>
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
