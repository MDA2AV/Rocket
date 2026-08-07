using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Threading.Tasks.Sources;
using ioxide.utils;

namespace ioxide.tls;

/// <summary>
/// A <see cref="PipeReader"/> that decrypts TLS records <b>in place, inside the ring buffer</b> and
/// hands that same memory to the caller - no owned plaintext buffer, no pump task, and no
/// <see cref="Pipe"/>.
///
/// The trick is that a memory BIO copies: <see cref="TlsSession.Feed"/> hands the ciphertext to
/// OpenSSL, and from that instant the recv buffer is dead as a <em>source</em> and free to be the
/// <em>destination</em>. It always fits, because a TLS 1.3 record spends 5 bytes of header, 1 byte
/// of content type and a 16-byte AEAD tag - so plaintext is at least 22 bytes shorter than the
/// ciphertext it came from (RFC 8446 section 5.2).
///
/// Compare <see cref="TlsConnectionDualPipe"/>, which decrypts into a Pipe it owns. This one keeps
/// the plaintext where the ciphertext landed, so backpressure is the ring itself: unconsumed
/// buffers are not returned, the ring runs dry, and the kernel stops filling. There is no pause
/// threshold to pick.
/// </summary>
/// <remarks>Reactor thread only. Mirrors TcpConnectionPipeReader's structure deliberately.</remarks>
public sealed unsafe class TlsInPlacePipeReader : PipeReader, IValueTaskSource<ReadResult>
{
    // A held slice is either ring memory decrypted in place, or - for the straddle case below -
    // a rented array. Both look identical to the caller; only the release path differs.
    private sealed class Slice : ReadOnlySequenceSegment<byte>
    {
        public readonly UnmanagedMemoryManager Manager = new(null, 0);
        public SpscRecvRing.Item Item;
        public byte[]? Owned;          // non-null => rented, return to the pool not the ring
        public Slice? NextSlice;

        public void SetRing(in SpscRecvRing.Item item, int plaintextLength)
        {
            Item = item;
            Owned = null;
            Manager.Reset(item.Ptr, plaintextLength, item.Bid, item.Gen);
            Memory = Manager.Memory;
            Reset();
        }

        public void SetOwned(byte[] buffer, int length)
        {
            Owned = buffer;
            Memory = buffer.AsMemory(0, length);
            Reset();
        }

        private void Reset()
        {
            Next = null;
            NextSlice = null;
            RunningIndex = 0;
        }

        public void Link(Slice next)
        {
            next.RunningIndex = RunningIndex + Memory.Length;
            Next = next;
            NextSlice = next;
        }
    }

    private readonly TcpConnection _conn;
    private readonly TlsSession _tls;

    private Slice? _head;
    private Slice? _tail;
    private int _headConsumed;
    private long _heldBytes;
    private long _examined;

    private readonly Stack<Slice> _pool = new();
    private ReadOnlySequence<byte> _lastSequence;

    private ManualResetValueTaskSourceCore<ReadResult> _core = new()
    {
        RunContinuationsAsynchronously = false,
    };
    private ValueTaskAwaiter<RecvSnapshot> _pendingRead;
    private readonly Action _onRecvReady;

    private bool _completed;
    private bool _cancelRequested;
    private bool _connectionClosed;
    private bool _prologueDone;

    public TlsInPlacePipeReader(TcpConnection connection, TlsSession session)
    {
        _conn = connection ?? throw new ArgumentNullException(nameof(connection));
        _tls = session ?? throw new ArgumentNullException(nameof(session));
        _onRecvReady = OnRecvReady;
    }

    public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfCompleted();
        TakeHandshakePlaintext();

        if (_cancelRequested)
        {
            _cancelRequested = false;
            return new ValueTask<ReadResult>(BuildResult(isCanceled: true));
        }

        if (_heldBytes > _examined || _connectionClosed)
        {
            return new ValueTask<ReadResult>(BuildResult(isCanceled: false));
        }

        while (true)
        {
            ValueTask<RecvSnapshot> pending = _conn.ReadAsync();

            if (!pending.IsCompletedSuccessfully)
            {
                _core.Reset();
                _pendingRead = pending.GetAwaiter();
                _pendingRead.UnsafeOnCompleted(_onRecvReady);
                return new ValueTask<ReadResult>(this, _core.Version);
            }

            if (Ingest(pending.Result) || _connectionClosed || _cancelRequested)
            {
                bool canceled = _cancelRequested;
                _cancelRequested = false;
                return new ValueTask<ReadResult>(BuildResult(canceled));
            }
        }
    }

    private void OnRecvReady()
    {
        RecvSnapshot snapshot = _pendingRead.GetResult();

        if (!Ingest(snapshot) && !_connectionClosed && !_cancelRequested)
        {
            while (true)
            {
                ValueTask<RecvSnapshot> pending = _conn.ReadAsync();

                if (!pending.IsCompletedSuccessfully)
                {
                    _pendingRead = pending.GetAwaiter();
                    _pendingRead.UnsafeOnCompleted(_onRecvReady);
                    return;
                }

                if (Ingest(pending.Result) || _connectionClosed || _cancelRequested)
                {
                    break;
                }
            }
        }

        bool canceled = _cancelRequested;
        _cancelRequested = false;
        _core.SetResult(BuildResult(canceled));
    }

    /// <summary>
    /// The client's first request usually rides in with its Finished flight, so the handshake
    /// already decrypted it and it is sitting in the session, not in any recv buffer. Miss this and
    /// the first request is dropped and the reader parks on bytes that already arrived.
    /// </summary>
    private void TakeHandshakePlaintext()
    {
        if (_prologueDone)
        {
            return;
        }
        _prologueDone = true;

        ReadOnlySpan<byte> initial = _tls.DrainPlaintext();
        if (initial.IsEmpty)
        {
            return;
        }

        byte[] owned = ArrayPool<byte>.Shared.Rent(initial.Length);
        initial.CopyTo(owned);

        Slice slice = Rent();
        slice.SetOwned(owned, initial.Length);
        Append(slice, initial.Length);
    }

    private bool Ingest(in RecvSnapshot snapshot)
    {
        bool any = false;

        while (_conn.TryGetItem(snapshot, out SpscRecvRing.Item item))
        {
            if (!item.HasBuffer)
            {
                continue;
            }

            // Hand the ciphertext over first: after this the buffer is only a destination.
            _tls.Feed(item.Ptr, item.Len);

            int produced = _tls.DrainInto(item.Ptr, item.Len, out bool more);

            if (produced == 0)
            {
                // A partial record, or a post-handshake message OpenSSL swallowed. Nothing for the
                // caller and the bytes live in the BIO now, so the buffer goes straight back.
                _conn.ReturnBuffer(in item);
            }
            else
            {
                Slice slice = Rent();
                slice.SetRing(in item, produced);
                Append(slice, produced);
                any = true;
            }

            // The buffer filled while the record layer still had plaintext. That happens when a
            // record straddles recvs and the buffer completing it is smaller than the plaintext it
            // releases - the one case in-place cannot serve, so the remainder spills to rented
            // memory. Rare, and correctness depends on it.
            while (more)
            {
                byte[] owned = ArrayPool<byte>.Shared.Rent(TlsSession.MaxRecordPlaintext);
                int spilled;
                fixed (byte* p = owned)
                {
                    spilled = _tls.DrainInto(p, owned.Length, out more);
                }

                if (spilled == 0)
                {
                    ArrayPool<byte>.Shared.Return(owned);
                    break;
                }

                Slice spill = Rent();
                spill.SetOwned(owned, spilled);
                Append(spill, spilled);
                any = true;
            }
        }

        _conn.ResetRead();

        if (snapshot.IsClosed || _tls.Closed)
        {
            _connectionClosed = true;
        }

        return any;
    }

    private Slice Rent() => _pool.TryPop(out Slice? pooled) ? pooled : new Slice();

    private void Append(Slice slice, int length)
    {
        if (_tail == null)
        {
            _head = _tail = slice;
            _headConsumed = 0;
        }
        else
        {
            _tail.Link(slice);
            _tail = slice;
        }

        _heldBytes += length;
    }

    private void Release(Slice slice)
    {
        if (slice.Owned is { } owned)
        {
            ArrayPool<byte>.Shared.Return(owned);
            slice.Owned = null;
        }
        else
        {
            _conn.ReturnBuffer(in slice.Item);
        }

        _pool.Push(slice);
    }

    private ReadResult BuildResult(bool isCanceled)
    {
        _lastSequence = _head == null
            ? default
            : new ReadOnlySequence<byte>(_head, _headConsumed, _tail!, _tail!.Memory.Length);

        return new ReadResult(_lastSequence, isCanceled, _connectionClosed);
    }

    public override bool TryRead(out ReadResult result)
    {
        ThrowIfCompleted();
        TakeHandshakePlaintext();

        if (_cancelRequested)
        {
            _cancelRequested = false;
            result = BuildResult(isCanceled: true);
            return true;
        }

        if (_heldBytes > _examined || _connectionClosed)
        {
            result = BuildResult(isCanceled: false);
            return true;
        }

        result = default;
        return false;
    }

    public override void AdvanceTo(SequencePosition consumed) => AdvanceTo(consumed, consumed);

    public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
    {
        if (_head == null)
        {
            return;
        }

        // Rebase by the sequence start: GetOffset measures from the head SEGMENT, so a partially
        // consumed head would over-count by _headConsumed and drive _heldBytes negative.
        long startOffset = _lastSequence.GetOffset(_lastSequence.Start);
        long consumedBytes = _lastSequence.GetOffset(consumed) - startOffset;
        long examinedBytes = _lastSequence.GetOffset(examined) - startOffset;
        if (examinedBytes < consumedBytes)
        {
            examinedBytes = consumedBytes;
        }

        long remaining = consumedBytes;
        while (remaining > 0 && _head != null)
        {
            int available = _head.Memory.Length - _headConsumed;

            if (remaining >= available)
            {
                Slice released = _head;
                _head = released.NextSlice;
                if (_head == null)
                {
                    _tail = null;
                }
                _headConsumed = 0;
                Release(released);

                remaining -= available;
            }
            else
            {
                _headConsumed += (int)remaining;
                remaining = 0;
            }
        }

        _heldBytes -= consumedBytes;
        _examined = Math.Min(examinedBytes - consumedBytes, _heldBytes);
    }

    public override void CancelPendingRead() => _cancelRequested = true;

    public override void Complete(Exception? exception = null)
    {
        if (_completed)
        {
            return;
        }

        _completed = true;

        while (_head != null)
        {
            Slice released = _head;
            _head = released.NextSlice;
            Release(released);
        }

        _tail = null;
        _headConsumed = 0;
        _heldBytes = 0;
        _examined = 0;
    }

    private void ThrowIfCompleted()
    {
        if (_completed)
        {
            throw new InvalidOperationException("Reading is not allowed after the reader was completed.");
        }
    }

    ReadResult IValueTaskSource<ReadResult>.GetResult(short token) => _core.GetResult(token);

    ValueTaskSourceStatus IValueTaskSource<ReadResult>.GetStatus(short token) => _core.GetStatus(token);

    void IValueTaskSource<ReadResult>.OnCompleted(
        Action<object?> continuation,
        object? state,
        short token,
        ValueTaskSourceOnCompletedFlags flags)
    {
        // Reactor thread only - strip the context-post so resumes stay inline.
        _core.OnCompleted(continuation, state, token,
            flags & ~ValueTaskSourceOnCompletedFlags.UseSchedulingContext);
    }
}
