using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks.Sources;
using static ioxide.pg.Native;

namespace ioxide.pg;

/// <summary>
/// A Postgres connection that runs entirely on a host io_uring reactor.
///
/// The TCP connect and the startup handshake happen once, with blocking syscalls, when the
/// connection is opened. After that, every query is an IORING_OP_SEND of the query followed by one
/// or more IORING_OP_RECV of the response — both submitted on the host's ring and awaited through
/// this object's value-task source. Because that source completes its continuations synchronously,
/// a query resumes inline on the reactor thread: no .NET socket engine, no thread pool.
///
/// A single connection is a single conversation, so it carries one in-flight query at a time. For
/// concurrency, open several connections and pool them.
/// </summary>
public sealed class PgConnection : IRingCompletion, IValueTaskSource<int>
{
    private const int BufferSize = 32 * 1024;

    private readonly IRingHost _host;
    private readonly nint _sendBuffer;
    private readonly nint _recvBuffer;

    private int _received;

    private ManualResetValueTaskSourceCore<int> _completion = new()
    {
        RunContinuationsAsynchronously = false
    };

    public int Fd { get; }

    private PgConnection(IRingHost host, int fd, nint sendBuffer, nint recvBuffer)
    {
        _host = host;
        Fd = fd;
        _sendBuffer = sendBuffer;
        _recvBuffer = recvBuffer;
    }

    /// <summary>
    /// Connect to Postgres on 127.0.0.1, run the trust-auth startup handshake, and bind the
    /// connection to <paramref name="host"/> so that its completions route here. This is blocking
    /// and one-time — call it at reactor startup, before serving.
    /// </summary>
    public static unsafe PgConnection Connect(IRingHost host, string user, string database, ushort port = 5432)
    {
        int fd = socket(AF_INET, SOCK_STREAM, 0);

        sockaddr_in address = default;
        address.sin_family = AF_INET;
        address.sin_port = Htons(port);
        address.sin_addr = 0x0100007F;   // 127.0.0.1, already in network byte order

        if (connect(fd, &address, (uint)sizeof(sockaddr_in)) < 0)
            throw new InvalidOperationException("Postgres connect failed.");

        // Small queries must not wait on Nagle to coalesce; that would add a ~40ms delayed-ACK
        // stall to every request.
        int on = 1;
        setsockopt(fd, IPPROTO_TCP, TCP_NODELAY, &on, sizeof(int));

        nint sendBuffer = (nint)NativeMemory.Alloc(BufferSize);
        nint recvBuffer = (nint)NativeMemory.Alloc(BufferSize);

        int startupLength = Pg.Startup((byte*)sendBuffer, user, database);
        Native.send(fd, (void*)sendBuffer, (nuint)startupLength, 0);

        // Read until ReadyForQuery so we know the server has finished authenticating and is idle.
        int received = 0;

        while (true)
        {
            long n = Native.recv(fd, (void*)(recvBuffer + received), (nuint)(BufferSize - received), 0);

            if (n <= 0)
                throw new InvalidOperationException("Postgres startup handshake failed.");

            received += (int)n;

            if (Pg.TryParse(new ReadOnlySpan<byte>((void*)recvBuffer, received), out _, out _))
                break;
        }

        var connection = new PgConnection(host, fd, sendBuffer, recvBuffer);
        host.Bind(fd, connection);

        return connection;
    }

    /// <summary>
    /// Run a simple query and return the first column of the first row as text, or an empty string
    /// if the result had no rows.
    /// </summary>
    public async Task<string> QueryAsync(string sql)
    {
        int queryLength = BuildQuery(sql);

        await SendQuery(queryLength);

        _received = 0;

        // The response can arrive across several recvs; keep reading until ReadyForQuery shows up.
        while (true)
        {
            int n = await ReceiveRows();

            if (n <= 0)
                return "";

            if (TryReadResult(n, out string value))
                return value;
        }
    }

    private unsafe int BuildQuery(string sql)
    {
        return Pg.Query((byte*)_sendBuffer, sql);
    }

    private ValueTask<int> SendQuery(int length)
    {
        _completion.Reset();

        var pending = new ValueTask<int>(this, _completion.Version);

        _host.SubmitSend(Fd, _sendBuffer, length);

        return pending;
    }

    private ValueTask<int> ReceiveRows()
    {
        _completion.Reset();

        var pending = new ValueTask<int>(this, _completion.Version);

        _host.SubmitRecv(Fd, _recvBuffer + _received, BufferSize - _received);

        return pending;
    }

    private unsafe bool TryReadResult(int n, out string value)
    {
        _received += n;

        bool ready = Pg.TryParse(
            new ReadOnlySpan<byte>((void*)_recvBuffer, _received),
            out int fieldStart,
            out int fieldLength);

        if (!ready)
        {
            value = "";
            return false;
        }

        value = fieldLength > 0
            ? Encoding.ASCII.GetString((byte*)(_recvBuffer + fieldStart), fieldLength)
            : "";

        return true;
    }

    // ── The reactor calls this when a SEND or RECV on our fd completes ───────────────────────

    public void Complete(int result)
    {
        _completion.SetResult(result);
    }

    int IValueTaskSource<int>.GetResult(short token)
    {
        return _completion.GetResult(token);
    }

    ValueTaskSourceStatus IValueTaskSource<int>.GetStatus(short token)
    {
        return _completion.GetStatus(token);
    }

    void IValueTaskSource<int>.OnCompleted(
        Action<object?> continuation,
        object? state,
        short token,
        ValueTaskSourceOnCompletedFlags flags)
    {
        _completion.OnCompleted(continuation, state, token, flags);
    }
}
