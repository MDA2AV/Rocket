using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks.Sources;
using static RingPg.Native;

namespace RingPg;

/// <summary>
/// A Postgres connection that runs on a host io_uring reactor (<see cref="IRingHost"/>). The
/// startup handshake is blocking and one-time; every query after that is SEND/RECV on the host's
/// ring, awaited via this object's IVTS (RCA=false) — so a query resumes inline on the reactor,
/// with no .NET socket engine and no thread pool.
///
/// Host-agnostic: give it any <see cref="IRingHost"/> (Minima, loom, …). Not declared `unsafe`,
/// so <see cref="QueryAsync"/> can `await`; the pointer work lives in unsafe helpers and the
/// buffers are held as <see cref="nint"/> (which cross awaits freely).
/// </summary>
public sealed class PgConnection : IRingCompletion, IValueTaskSource<int>
{
    public const int Buf = 32 * 1024;

    private readonly IRingHost _host;
    private readonly nint _send;
    private readonly nint _recv;
    private int _recvLen;
    private ManualResetValueTaskSourceCore<int> _io = new() { RunContinuationsAsynchronously = false };

    public int Fd { get; }

    private PgConnection(IRingHost host, int fd, nint send, nint recv)
    {
        _host = host; Fd = fd; _send = send; _recv = recv;
    }

    /// <summary>Blocking connect to 127.0.0.1:port, StartupMessage, read to ReadyForQuery, then
    /// bind to the host so its CQEs route here. One-time, typically at reactor startup.</summary>
    public static unsafe PgConnection Connect(IRingHost host, string user, string db, ushort port = 5432)
    {
        int fd = socket(AF_INET, SOCK_STREAM, 0);
        sockaddr_in addr = default;
        addr.sin_family = AF_INET;
        addr.sin_port = Htons(port);
        addr.sin_addr = 0x0100007F;                  // 127.0.0.1, network byte order
        if (connect(fd, &addr, (uint)sizeof(sockaddr_in)) < 0) throw new Exception("pg connect failed");
        int one = 1;
        setsockopt(fd, IPPROTO_TCP, TCP_NODELAY, &one, sizeof(int));   // small queries must not wait on Nagle

        nint send = (nint)NativeMemory.Alloc(Buf);
        nint recv = (nint)NativeMemory.Alloc(Buf);

        int len = Pg.Startup((byte*)send, user, db);
        Native.send(fd, (void*)send, (nuint)len, 0);

        int rl = 0;
        while (true)
        {
            long n = Native.recv(fd, (void*)(recv + rl), (nuint)(Buf - rl), 0);
            if (n <= 0) throw new Exception("pg startup recv failed");
            rl += (int)n;
            if (Pg.TryParse(new ReadOnlySpan<byte>((void*)recv, rl), out _, out _, out bool ready) && ready) break;
        }

        var c = new PgConnection(host, fd, send, recv);
        host.Bind(fd, c);
        return c;
    }

    /// <summary>Run a simple query; returns the first DataRow's first field as text ("" if none).</summary>
    public async Task<string> QueryAsync(string sql)
    {
        int len = BuildQuery(sql);
        await SendOp(len);
        _recvLen = 0;
        while (true)
        {
            int n = await RecvOp();
            if (n <= 0) return "";
            if (Finish(n, out string result)) return result;
        }
    }

    private unsafe int BuildQuery(string sql) => Pg.Query((byte*)_send, sql);

    private ValueTask<int> SendOp(int len)
    {
        _io.Reset();
        var vt = new ValueTask<int>(this, _io.Version);
        _host.SubmitSend(Fd, _send, len);
        return vt;
    }

    private ValueTask<int> RecvOp()
    {
        _io.Reset();
        var vt = new ValueTask<int>(this, _io.Version);
        _host.SubmitRecv(Fd, _recv + _recvLen, Buf - _recvLen);
        return vt;
    }

    private unsafe bool Finish(int n, out string result)
    {
        _recvLen += n;
        if (Pg.TryParse(new ReadOnlySpan<byte>((void*)_recv, _recvLen), out int fs, out int fl, out bool ready) && ready)
        {
            result = fl > 0 ? Encoding.ASCII.GetString((byte*)(_recv + fs), fl) : "";
            return true;
        }
        result = "";
        return false;
    }

    public void Complete(int result) => _io.SetResult(result);

    int IValueTaskSource<int>.GetResult(short t) => _io.GetResult(t);
    ValueTaskSourceStatus IValueTaskSource<int>.GetStatus(short t) => _io.GetStatus(t);
    void IValueTaskSource<int>.OnCompleted(Action<object?> c, object? s, short t, ValueTaskSourceOnCompletedFlags f)
        => _io.OnCompleted(c, s, t, f);
}
