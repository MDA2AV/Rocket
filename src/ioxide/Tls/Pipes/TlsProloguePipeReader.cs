using System.Buffers;
using System.IO.Pipelines;

namespace ioxide.tls;

/// <summary>
/// Bridges the one gap kTLS RX leaves: plaintext the <i>handshake</i> decrypted lives in the
/// session, not in ring memory, so a reader that only knows about ring memory would never yield it.
///
/// The client's first request routinely rides in with its Finished flight - for HTTP/2 that is the
/// connection preface, so it is the common case rather than an edge one. Miss it and the peer waits
/// forever on a response to a request the server dropped.
///
/// This serves that carry first, then <b>gets out of the way</b>: once the caller has consumed past
/// it, every later read delegates straight to <paramref name="inner"/> with no copy and no
/// bookkeeping. It is a startup detour, not a pump.
/// </summary>
/// <remarks>Reactor thread only.</remarks>
public sealed class TlsProloguePipeReader : PipeReader
{
    private readonly PipeReader _inner;

    private byte[] _carry;
    private int _length;
    private int _consumed;
    private bool _drained;      // the carry is gone; pure delegation from here
    private bool _completed;

    public TlsProloguePipeReader(PipeReader inner, ReadOnlySpan<byte> prologue)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));

        if (prologue.IsEmpty)
        {
            _carry = [];
            _drained = true;
            return;
        }

        _carry = ArrayPool<byte>.Shared.Rent(prologue.Length);
        prologue.CopyTo(_carry);
        _length = prologue.Length;
    }

    /// <summary>True once this is a pass-through and costs nothing.</summary>
    public bool Drained => _drained;

    public override async ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (_drained)
        {
            return await _inner.ReadAsync(cancellationToken);
        }

        // Unconsumed carry first. A caller that examined it all without consuming needs MORE than
        // the prologue to make progress, so pull from the connection and append - otherwise the
        // same bytes would be handed back forever.
        if (_consumed < _length)
        {
            return new ReadResult(Sequence(), isCanceled: false, isCompleted: false);
        }

        ReadResult next = await _inner.ReadAsync(cancellationToken);
        Append(next.Buffer);
        _inner.AdvanceTo(next.Buffer.End);

        return new ReadResult(Sequence(), next.IsCanceled, next.IsCompleted);
    }

    public override bool TryRead(out ReadResult result)
    {
        if (_drained)
        {
            return _inner.TryRead(out result);
        }

        if (_consumed < _length)
        {
            result = new ReadResult(Sequence(), isCanceled: false, isCompleted: false);
            return true;
        }

        result = default;
        return false;
    }

    public override void AdvanceTo(SequencePosition consumed) => AdvanceTo(consumed, consumed);

    public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
    {
        if (_drained)
        {
            _inner.AdvanceTo(consumed, examined);
            return;
        }

        // Single segment while the carry is live, so the position is the offset into it.
        _consumed += consumed.GetInteger();

        if (_consumed < _length)
        {
            return;
        }

        // Fully consumed. Release the carry and become a pass-through; anything that arrives from
        // here on is ring memory the inner reader hands out directly.
        Release();
    }

    public override void CancelPendingRead()
    {
        _inner.CancelPendingRead();
    }

    public override void Complete(Exception? exception = null)
    {
        if (_completed)
        {
            return;
        }
        _completed = true;

        Release();
        _inner.Complete(exception);
    }

    private ReadOnlySequence<byte> Sequence() => new(_carry.AsMemory(_consumed, _length - _consumed));

    private void Append(in ReadOnlySequence<byte> more)
    {
        int extra = (int)more.Length;
        if (extra == 0)
        {
            return;
        }

        // Compact first: the consumed prefix is dead weight and this only happens while the
        // prologue is still being worked through.
        if (_consumed > 0)
        {
            _carry.AsSpan(_consumed, _length - _consumed).CopyTo(_carry);
            _length -= _consumed;
            _consumed = 0;
        }

        if (_carry.Length - _length < extra)
        {
            byte[] grown = ArrayPool<byte>.Shared.Rent(Math.Max(_carry.Length * 2, _length + extra));
            _carry.AsSpan(0, _length).CopyTo(grown);
            ArrayPool<byte>.Shared.Return(_carry);
            _carry = grown;
        }

        more.CopyTo(_carry.AsSpan(_length));
        _length += extra;
    }

    private void Release()
    {
        if (_carry.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_carry);
        }
        _carry = [];
        _length = 0;
        _consumed = 0;
        _drained = true;
    }
}
