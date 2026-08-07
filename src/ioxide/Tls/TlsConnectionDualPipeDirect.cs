using System.Buffers;
using System.IO.Pipelines;
using ioxide.utils;

namespace ioxide.tls;

/// <summary>
/// <see cref="TcpConnectionDualPipe"/> with one thing changed: OpenSSL decrypts the recv buffer
/// before the caller sees it. No pump, no second <see cref="Pipe"/>, no chaining - the plaintext is
/// handed out from the same memory the ciphertext arrived in.
///
/// It works because a memory BIO copies. Once <see cref="TlsSession.Feed"/> hands the ciphertext to
/// OpenSSL the buffer is dead as a source and free to be the destination, and the plaintext always
/// fits: a TLS 1.3 record spends 5 bytes of header, 1 of content type and a 16-byte tag, so it is
/// at least 22 bytes shorter than the ciphertext it came from (RFC 8446 section 5.2).
///
/// One buffer is held at a time. That is not a simplification with a cost - it is what removes the
/// only hard case. A record can straddle recvs, so the buffer completing one may release more
/// plaintext than it holds; here the held buffer is simply drained again once the caller has
/// consumed it, so the overflow needs no spill array and no second code path.
/// </summary>
/// <remarks>Reactor thread only. Experimental.</remarks>
public sealed class TlsConnectionDualPipeDirect : IDuplexPipe, IAsyncDisposable
{
    private readonly TlsSession _tls;
    private readonly bool _ownsSession;
    private readonly TlsDirectPipeReader _inbound;
    private readonly TcpConnectionDualPipe _outbound;   // only its writer is used

    public TlsConnectionDualPipeDirect(TcpConnection connection, TlsSession session,
        bool ownsSession = true)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(session);

        _tls = session;
        _ownsSession = ownsSession;
        _inbound = new TlsDirectPipeReader(connection, session);
        _outbound = new TcpConnectionDualPipe(connection);
    }

    /// <summary>Decrypted request bytes, in the buffer they arrived in.</summary>
    public PipeReader Input => _inbound;

    /// <summary>Response bytes, written as PLAINTEXT - kTLS makes the records.</summary>
    public PipeWriter Output => _outbound.Output;

    public ValueTask DisposeAsync()
    {
        _inbound.Complete();

        if (_ownsSession)
        {
            _tls.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// The read half of <see cref="TlsConnectionDualPipeDirect"/>: one recv buffer at a time,
/// decrypted where it lies.
/// </summary>
public sealed class TlsDirectPipeReader : PipeReader
{
    private readonly TcpConnection _conn;
    private readonly TlsSession _tls;

    // The single held buffer, its plaintext length, and how much the caller has taken.
    private SpscRecvRing.Item _item;
    private bool _held;
    private int _length;
    private int _consumed;

    // The handshake's final flight usually carries the first request, and that plaintext lives in
    // the session rather than in any recv buffer - so it is the one thing that needs a copy.
    private byte[]? _prologue;
    private bool _prologueTaken;

    private bool _closed;
    private bool _canceled;
    private bool _completed;

    internal TlsDirectPipeReader(TcpConnection connection, TlsSession session)
    {
        _conn = connection;
        _tls = session;
    }

    public override async ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_completed, this);

        while (true)
        {
            if (TryBuild(out ReadResult ready))
            {
                return ready;
            }

            if (_closed || _tls.Closed)
            {
                return new ReadResult(default, isCanceled: Take(ref _canceled), isCompleted: true);
            }

            RecvSnapshot snapshot = await _conn.ReadAsync();

            TakeOne(snapshot);
            _conn.ResetRead();

            if (snapshot.IsClosed)
            {
                _closed = true;
            }
        }
    }

    /// <summary>
    /// Decrypt one recv buffer in place and hold it. Whatever else the snapshot carries stays
    /// queued: TcpConnection.ReadAsync completes without waiting while its recv queue is non-empty,
    /// so the next pass picks it up synchronously.
    /// </summary>
    private unsafe void TakeOne(in RecvSnapshot snapshot)
    {
        while (!_held && _conn.TryGetItem(snapshot, out SpscRecvRing.Item item))
        {
            if (!item.HasBuffer)
            {
                continue;
            }

            _tls.Feed(item.Ptr, item.Len);            // OpenSSL now owns a copy of the ciphertext
            int produced = _tls.DrainInto(item.Ptr, item.Len, out _);

            if (produced == 0)
            {
                // A partial record, or a post-handshake message. Nothing to hand out and the bytes
                // live in the BIO now, so the buffer goes straight back.
                _conn.ReturnBuffer(in item);
                continue;
            }

            _item = item;
            _held = true;
            _length = produced;
            _consumed = 0;
        }
    }

    // The prologue first, then whatever the held buffer still has. Returns false when the caller
    // must wait for more ciphertext.
    private unsafe bool TryBuild(out ReadResult result)
    {
        if (!_prologueTaken)
        {
            _prologueTaken = true;
            ReadOnlySpan<byte> initial = _tls.DrainPlaintext();

            if (!initial.IsEmpty)
            {
                _prologue = ArrayPool<byte>.Shared.Rent(initial.Length);
                initial.CopyTo(_prologue);
                _length = initial.Length;
                _consumed = 0;
            }
        }

        int available = _length - _consumed;
        if (available <= 0)
        {
            result = default;
            return false;
        }

        ReadOnlyMemory<byte> plaintext = _prologue is not null
            ? _prologue.AsMemory(_consumed, available)
            : new UnmanagedMemoryManager(_item.Ptr + _consumed, available).Memory;

        result = new ReadResult(new ReadOnlySequence<byte>(plaintext),
            isCanceled: Take(ref _canceled), isCompleted: false);
        return true;
    }

    public override void AdvanceTo(SequencePosition consumed) => AdvanceTo(consumed, consumed);

    public override unsafe void AdvanceTo(SequencePosition consumed, SequencePosition examined)
    {
        // Single segment, so the position IS the offset into what the last read handed out.
        _consumed += consumed.GetInteger();

        if (_consumed < _length)
        {
            return;   // the caller left some behind; the next read hands back the rest
        }

        _consumed = 0;
        _length = 0;

        if (_prologue is not null)
        {
            ArrayPool<byte>.Shared.Return(_prologue);
            _prologue = null;
            return;
        }

        if (!_held)
        {
            return;
        }

        // The buffer is free again - and a record that straddled recvs may have released more
        // plaintext than it could hold, so drain into it once more before giving it up. This is
        // what replaces a spill buffer.
        int more = _tls.DrainInto(_item.Ptr, _item.Len, out _);
        if (more > 0)
        {
            _length = more;
            return;
        }

        _conn.ReturnBuffer(in _item);
        _held = false;
    }

    public override bool TryRead(out ReadResult result) => TryBuild(out result);

    public override void CancelPendingRead() => _canceled = true;

    public override unsafe void Complete(Exception? exception = null)
    {
        if (_completed)
        {
            return;
        }
        _completed = true;

        if (_prologue is not null)
        {
            ArrayPool<byte>.Shared.Return(_prologue);
            _prologue = null;
        }

        if (_held)
        {
            _conn.ReturnBuffer(in _item);
            _held = false;
        }
    }

    private static bool Take(ref bool flag)
    {
        bool value = flag;
        flag = false;
        return value;
    }
}
