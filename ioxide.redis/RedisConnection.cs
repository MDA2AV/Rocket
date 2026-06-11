using System.Runtime.InteropServices;
using System.Text;

namespace ioxide.redis;

/// <summary>
/// One Redis connection on a reactor's ring. Connect, auth, and every command run as ring ops with
/// inline completion resume. The protocol surface is generic - <see cref="ExecuteAsync(string, RedisArg[])"/>
/// runs any command - with typed helpers (in RedisConnection.Commands.cs) layered on top.
/// </summary>
public sealed partial class RedisConnection : IDisposable
{
    private const int InitialBufferSize = 16 * 1024;
    private const int MaxBufferSize = 64 * 1024 * 1024;

    private readonly RingSocket _socket;

    private nint _send;
    private int _sendCapacity;
    private nint _recv;
    private int _recvCapacity;
    private int _received;   // valid bytes in _recv
    private int _scan;       // start of the first unparsed reply

    public bool IsBroken { get; private set; }

    private unsafe RedisConnection(RingSocket socket)
    {
        _socket = socket;
        _send = (nint)NativeMemory.Alloc(InitialBufferSize);
        _sendCapacity = InitialBufferSize;
        _recv = (nint)NativeMemory.Alloc(InitialBufferSize);
        _recvCapacity = InitialBufferSize;
    }

    public static async Task<RedisConnection> ConnectAsync(IRingHost host, RedisOptions options)
    {
        RingSocket socket = RingSocket.CreateTcp(host);
        var connection = new RedisConnection(socket);

        try
        {
            int rc = await socket.ConnectAsync(options.Host, options.Port);
            if (rc < 0)
            {
                throw RedisException.Transport($"connect to {options.Host}:{options.Port}", rc);
            }

            if (!string.IsNullOrEmpty(options.Password))
            {
                _ = options.User is { } user
                    ? await connection.ExecuteAsync("AUTH", user, options.Password)
                    : await connection.ExecuteAsync("AUTH", options.Password);
            }
            if (options.Database != 0)
            {
                await connection.ExecuteAsync("SELECT", options.Database);
            }

            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>Run any command and return its reply. Throws on a top-level error reply.</summary>
    public async ValueTask<RespValue> ExecuteAsync(string command, params RedisArg[] args)
    {
        if (IsBroken)
        {
            throw new RedisException("connection is broken");
        }

        WriteCommand(command, args);
        await SendAllAsync(_pendingSend);

        RespValue reply = await ReceiveReplyAsync();
        if (reply.IsError)
        {
            throw new RedisException(reply.AsString() ?? "redis error");
        }
        return reply;
    }

    /// <summary>
    /// Send several commands back to back and read all replies in one round trip. Each element of
    /// <paramref name="commands"/> is (command, args). Error replies are returned, not thrown, so
    /// the caller can inspect per-command outcomes.
    /// </summary>
    public async ValueTask<RespValue[]> PipelineAsync(params RedisCommand[] commands)
    {
        if (IsBroken)
        {
            throw new RedisException("connection is broken");
        }

        int total = 0;
        foreach (RedisCommand c in commands)
        {
            total += RespProtocol.CommandSize(c.NameBytes, c.Args);
        }
        EnsureSendCapacity(total);

        int p = 0;
        foreach (RedisCommand c in commands)
        {
            p += WriteCommandAt(p, c.NameBytes, c.Args);
        }
        await SendAllAsync(p);

        var replies = new RespValue[commands.Length];
        for (int i = 0; i < commands.Length; i++)
        {
            replies[i] = await ReceiveReplyAsync();
        }
        return replies;
    }

    // -- wire I/O ---------------------------------------------------------

    private int _pendingSend;

    private unsafe void WriteCommand(string command, RedisArg[] args)
    {
        ReadOnlySpan<byte> name = Encoding.ASCII.GetBytes(command);
        int size = RespProtocol.CommandSize(name, args);
        EnsureSendCapacity(size);
        _pendingSend = RespProtocol.WriteCommand(new Span<byte>((void*)_send, _sendCapacity), name, args);
    }

    private unsafe int WriteCommandAt(int offset, byte[] name, RedisArg[] args) =>
        RespProtocol.WriteCommand(new Span<byte>((void*)(_send + offset), _sendCapacity - offset), name, args);

    private unsafe void EnsureSendCapacity(int needed)
    {
        if (needed <= _sendCapacity)
        {
            return;
        }
        int cap = _sendCapacity;
        while (cap < needed)
        {
            cap *= 2;
        }
        _send = (nint)NativeMemory.Realloc((void*)_send, (nuint)cap);
        _sendCapacity = cap;
    }

    private async ValueTask SendAllAsync(int length)
    {
        int sent = 0;
        while (sent < length)
        {
            int n = await _socket.SendAsync(_send + sent, length - sent);
            if (n <= 0)
            {
                IsBroken = true;
                throw RedisException.Transport("send", n);
            }
            sent += n;
        }
    }

    private async ValueTask<RespValue> ReceiveReplyAsync()
    {
        while (true)
        {
            if (TryParseReply(out RespValue reply))
            {
                return reply;
            }

            if (_scan == _received)
            {
                _scan = 0;
                _received = 0;
            }

            EnsureRecvSpace();

            int n = await _socket.RecvAsync(_recv + _received, _recvCapacity - _received);
            if (n <= 0)
            {
                IsBroken = true;
                throw RedisException.Transport("recv", n);
            }
            _received += n;
        }
    }

    private unsafe bool TryParseReply(out RespValue reply)
    {
        var buffered = new ReadOnlySpan<byte>((void*)(_recv + _scan), _received - _scan);
        if (RespProtocol.TryParse(buffered, out reply, out int consumed))
        {
            _scan += consumed;
            return true;
        }
        return false;
    }

    private unsafe void EnsureRecvSpace()
    {
        if (_received < _recvCapacity)
        {
            return;
        }
        if (_scan > 0)
        {
            Buffer.MemoryCopy((void*)(_recv + _scan), (void*)_recv, _recvCapacity, _received - _scan);
            _received -= _scan;
            _scan = 0;
            return;
        }
        if (_recvCapacity >= MaxBufferSize)
        {
            IsBroken = true;
            throw new RedisException($"reply exceeds {MaxBufferSize} bytes");
        }
        _recvCapacity *= 2;
        _recv = (nint)NativeMemory.Realloc((void*)_recv, (nuint)_recvCapacity);
    }

    public unsafe void Dispose()
    {
        _socket.Dispose();
        if (_send != 0) { NativeMemory.Free((void*)_send); _send = 0; }
        if (_recv != 0) { NativeMemory.Free((void*)_recv); _recv = 0; }
    }
}

/// <summary>A command for <see cref="RedisConnection.PipelineAsync"/>: a name and its arguments.</summary>
public readonly struct RedisCommand
{
    internal readonly byte[] NameBytes;
    internal readonly RedisArg[] Args;

    public RedisCommand(string name, params RedisArg[] args)
    {
        NameBytes = Encoding.ASCII.GetBytes(name);
        Args = args;
    }
}
