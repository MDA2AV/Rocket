using System.Runtime.InteropServices;
using System.Text;

namespace ioxide.pg;

/// <summary>
/// A Postgres connection that runs entirely on the host's ring - connect and handshake included,
/// so opening one never blocks the reactor. One query in flight at a time; concurrency comes from
/// <see cref="PgPool"/>. A server ErrorResponse throws but leaves the connection usable (the
/// stream resyncs at ReadyForQuery); a transport failure marks it <see cref="IsBroken"/> and the
/// pool replaces it.
/// </summary>
public sealed class PgConnection : IDisposable
{
    private const int InitialBufferSize = 64 * 1024;
    private const int MaxBufferSize = 1 << 20;

    private readonly RingSocket _socket;

    // Native wire buffers. Kept as nint (not byte*) so the async protocol flow can do address
    // arithmetic without an unsafe context; only the small parse/format helpers touch memory.
    private nint _send;
    private int _sendCapacity;
    private nint _recv;
    private int _recvCapacity;

    private int _received;   // valid bytes in _recv
    private int _scan;       // start of the first unparsed message

    /// <summary>Transport-level failure happened; the connection must be discarded, not reused.</summary>
    public bool IsBroken { get; private set; }

    private unsafe PgConnection(RingSocket socket)
    {
        _socket = socket;
        _send = (nint)NativeMemory.Alloc(InitialBufferSize);
        _sendCapacity = InitialBufferSize;
        _recv = (nint)NativeMemory.Alloc(InitialBufferSize);
        _recvCapacity = InitialBufferSize;
    }

    /// <summary>
    /// Open a connection over the ring: connect, send the startup message, and consume the
    /// handshake until ReadyForQuery. Trust authentication only for now - anything else fails
    /// with a clear error rather than a silent hang.
    /// </summary>
    public static async Task<PgConnection> ConnectAsync(IRingHost host, PgOptions options)
    {
        RingSocket socket = RingSocket.CreateTcp(host);
        var connection = new PgConnection(socket);

        try
        {
            int rc = await socket.ConnectAsync(options.Host, options.Port);
            if (rc < 0)
            {
                throw PgException.Transport($"connect to {options.Host}:{options.Port}", rc);
            }

            await connection.StartupAsync(options);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private async Task StartupAsync(PgOptions options)
    {
        int length = WriteStartup(options.User, options.Database);
        await SendAllAsync(length);

        PgScram? scram = null;

        while (true)
        {
            Message message = await ReceiveMessageAsync();

            switch (message.Tag)
            {
                case PgProtocol.Authentication:
                    int code = ReadAuthCode(message);
                    if (code == 0)
                    {
                        break;   // AuthenticationOk
                    }
                    if (code == 10)
                    {
                        // SASL: the server lists mechanisms; we speak SCRAM-SHA-256.
                        if (!ScramOffered(message))
                        {
                            throw new PgException("server offers SASL but not SCRAM-SHA-256");
                        }
                        scram = new PgScram(options.Password
                            ?? throw new PgException("server requires SCRAM-SHA-256 but PgOptions.Password is not set"));
                        await SendAllAsync(WriteSaslInitial(scram.ClientFirst()));
                        break;
                    }
                    if (code == 11)
                    {
                        // SASLContinue: server-first in, client-final (with proof) out.
                        if (scram == null)
                        {
                            throw new PgException("unexpected SASLContinue");
                        }
                        await SendAllAsync(WriteSaslFinal(scram.ClientFinal(ReadAuthData(message))));
                        break;
                    }
                    if (code == 12)
                    {
                        // SASLFinal: verify the server signature.
                        if (scram == null)
                        {
                            throw new PgException("unexpected SASLFinal");
                        }
                        scram.VerifyServerFinal(ReadAuthData(message));
                        break;
                    }
                    throw new PgException($"authentication method {code} not supported (trust or SCRAM-SHA-256)");

                case PgProtocol.ErrorResponse:
                    throw ReadServerError(message);

                case PgProtocol.ReadyForQuery:
                    return;

                // ParameterStatus, BackendKeyData, NoticeResponse: skipped by the walker.
            }
        }
    }

    /// <summary>
    /// Run one simple query and collect its result (first column of the first row, the row count,
    /// and the command tag). Throws <see cref="PgException"/> on a server error - after consuming
    /// the rest of the response, so the connection stays usable.
    /// </summary>
    public async ValueTask<PgResult> QueryAsync(string sql)
    {
        if (IsBroken)
        {
            throw new PgException("connection is broken");
        }

        int length = WriteQuery(sql);
        await SendAllAsync(length);

        string? value = null;
        bool valueCaptured = false;
        int rows = 0;
        string commandTag = "";
        PgException? serverError = null;

        while (true)
        {
            Message message = await ReceiveMessageAsync();

            switch (message.Tag)
            {
                case PgProtocol.DataRow:
                    rows++;
                    if (!valueCaptured)
                    {
                        valueCaptured = true;
                        value = ReadFirstField(message);
                    }
                    break;

                case PgProtocol.CommandComplete:
                    commandTag = ReadBodyCString(message);
                    break;

                case PgProtocol.ErrorResponse:
                    // Don't throw yet: consume until ReadyForQuery so the stream
                    // is resynchronized and the connection can serve the next query.
                    serverError = ReadServerError(message);
                    break;

                case PgProtocol.ReadyForQuery:
                    if (serverError != null)
                    {
                        throw serverError;
                    }
                    return new PgResult(value, rows, commandTag);
            }
        }
    }

    private readonly record struct Message(byte Tag, int BodyStart, int BodyLength);

    private async ValueTask<Message> ReceiveMessageAsync()
    {
        while (true)
        {
            if (TryParseMessage(out Message message))
            {
                return message;
            }

            // Everything buffered is consumed - rewind so the buffer never fills
            // from pure accumulation across queries.
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
                throw PgException.Transport("recv", n);
            }
            _received += n;
        }
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
                throw PgException.Transport("send", n);
            }
            sent += n;
        }
    }

    private unsafe bool TryParseMessage(out Message message)
    {
        var buffered = new ReadOnlySpan<byte>((void*)_recv, _received);
        int position = _scan;

        try
        {
            if (PgProtocol.TryReadMessage(buffered, ref position, out byte tag, out int bodyStart, out int bodyLength))
            {
                _scan = position;
                message = new Message(tag, bodyStart, bodyLength);
                return true;
            }
        }
        catch (PgException)
        {
            // Malformed framing - the stream can't be resynchronized.
            IsBroken = true;
            throw;
        }

        message = default;
        return false;
    }

    private unsafe void EnsureRecvSpace()
    {
        if (_received < _recvCapacity)
        {
            return;
        }

        // Buffer is full with a partial message at the end. First reclaim the
        // consumed prefix; only grow when a single message outsizes the buffer.
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
            throw new PgException($"backend message exceeds {MaxBufferSize} bytes");
        }

        _recvCapacity *= 2;
        _recv = (nint)NativeMemory.Realloc((void*)_recv, (nuint)_recvCapacity);
    }

    /// <summary>
    /// Run one simple query, streaming every DataRow to <paramref name="onRow"/> (inline, on the
    /// reactor) and returning the row count. Server errors throw after the stream resyncs at
    /// ReadyForQuery, same as <see cref="QueryAsync"/>.
    /// </summary>
    public async ValueTask<int> QueryRowsAsync(string sql, PgRowHandler onRow)
    {
        if (IsBroken)
        {
            throw new PgException("connection is broken");
        }

        int length = WriteQuery(sql);
        await SendAllAsync(length);

        int rows = 0;
        PgException? serverError = null;

        while (true)
        {
            Message message = await ReceiveMessageAsync();

            switch (message.Tag)
            {
                case PgProtocol.DataRow:
                    rows++;
                    if (serverError == null)
                    {
                        InvokeRow(onRow, in message);
                    }
                    break;

                case PgProtocol.ErrorResponse:
                    serverError = ReadServerError(message);
                    break;

                case PgProtocol.ReadyForQuery:
                    if (serverError != null)
                    {
                        throw serverError;
                    }
                    return rows;
            }
        }
    }

    private unsafe void InvokeRow(PgRowHandler onRow, in Message message)
    {
        onRow(new PgRow(new ReadOnlySpan<byte>((void*)(_recv + message.BodyStart), message.BodyLength)));
    }

    private unsafe bool ScramOffered(in Message message)
    {
        ReadOnlySpan<byte> body = Body(message);
        return body.Length > 4 && PgProtocol.OffersScramSha256(body[4..]);
    }

    private unsafe string ReadAuthData(in Message message)
    {
        ReadOnlySpan<byte> body = Body(message);
        return Encoding.UTF8.GetString(body[4..]);
    }

    private unsafe int WriteSaslInitial(string clientFirst)
    {
        byte[] payload = Encoding.UTF8.GetBytes(clientFirst);
        return PgProtocol.WriteSaslInitialResponse(new Span<byte>((void*)_send, _sendCapacity), "SCRAM-SHA-256", payload);
    }

    private unsafe int WriteSaslFinal(string clientFinal)
    {
        byte[] payload = Encoding.UTF8.GetBytes(clientFinal);
        return PgProtocol.WriteSaslResponse(new Span<byte>((void*)_send, _sendCapacity), payload);
    }

    private unsafe int WriteStartup(string user, string database)
    {
        return PgProtocol.WriteStartup(new Span<byte>((void*)_send, _sendCapacity), user, database);
    }

    private unsafe int WriteQuery(string sql)
    {
        int needed = PgProtocol.QueryLength(sql);
        if (needed > _sendCapacity)
        {
            if (needed > MaxBufferSize)
            {
                throw new PgException($"query exceeds {MaxBufferSize} bytes");
            }
            int newCapacity = _sendCapacity;
            while (newCapacity < needed)
            {
                newCapacity *= 2;
            }
            _send = (nint)NativeMemory.Realloc((void*)_send, (nuint)newCapacity);
            _sendCapacity = newCapacity;
        }

        return PgProtocol.WriteQuery(new Span<byte>((void*)_send, _sendCapacity), sql);
    }

    private unsafe ReadOnlySpan<byte> Body(in Message message)
    {
        return new ReadOnlySpan<byte>((void*)(_recv + message.BodyStart), message.BodyLength);
    }

    private string? ReadFirstField(in Message message)
    {
        ReadOnlySpan<byte> body = Body(message);
        if (!PgProtocol.TryReadFirstField(body, out int offset, out int length) || length < 0)
        {
            return null;   // no fields, or SQL NULL
        }
        return Encoding.UTF8.GetString(body.Slice(offset, length));
    }

    private string ReadBodyCString(in Message message)
    {
        return PgProtocol.ReadCString(Body(message));
    }

    private int ReadAuthCode(in Message message)
    {
        return PgProtocol.ReadAuthCode(Body(message));
    }

    private PgException ReadServerError(in Message message)
    {
        (string severity, string sqlState, string text) = PgProtocol.ReadError(Body(message));
        return new PgException(severity, sqlState, text);
    }

    public unsafe void Dispose()
    {
        _socket.Dispose();

        if (_send != 0)
        {
            NativeMemory.Free((void*)_send);
            _send = 0;
        }
        if (_recv != 0)
        {
            NativeMemory.Free((void*)_recv);
            _recv = 0;
        }
    }
}
