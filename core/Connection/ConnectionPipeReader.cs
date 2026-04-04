using System.Buffers;
using System.IO.Pipelines;

namespace zerg.core;

public sealed class ConnectionPipeReader : PipeReader
{
    private readonly ConnectionBase _inner;

    private readonly List<HeldBuffer> _held = new(4);
    private ReadOnlySequence<byte> _lastSequence;

    private bool _completed;
    private bool _cancelRequested;
    private bool _connectionClosed;

    public ConnectionPipeReader(ConnectionBase inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public override async ValueTask<ReadResult> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfCompleted();

        if (_cancelRequested)
        {
            _cancelRequested = false;
            return new ReadResult(BuildSequence(), isCanceled: true, isCompleted: _connectionClosed);
        }

        if (_held.Count > 0)
            return new ReadResult(BuildSequence(), isCanceled: false, isCompleted: _connectionClosed);

        if (_connectionClosed)
            return new ReadResult(default, isCanceled: false, isCompleted: true);

        var result = await _inner.ReadAsync();

        if (result.IsClosed)
        {
            _connectionClosed = true;
            return new ReadResult(BuildSequence(), isCanceled: false, isCompleted: true);
        }

        DrainSnapshot(result);
        _inner.ResetRead();

        if (_cancelRequested)
        {
            _cancelRequested = false;
            return new ReadResult(BuildSequence(), isCanceled: true, isCompleted: false);
        }

        return new ReadResult(BuildSequence(), isCanceled: false, isCompleted: false);
    }

    public override bool TryRead(out ReadResult result)
    {
        ThrowIfCompleted();

        if (_cancelRequested)
        {
            _cancelRequested = false;
            result = new ReadResult(BuildSequence(), isCanceled: true, isCompleted: _connectionClosed);
            return true;
        }

        if (_held.Count > 0)
        {
            result = new ReadResult(BuildSequence(), isCanceled: false, isCompleted: _connectionClosed);
            return true;
        }

        if (_connectionClosed)
        {
            result = new ReadResult(default, isCanceled: false, isCompleted: true);
            return true;
        }

        result = default;
        return false;
    }

    public override void AdvanceTo(SequencePosition consumed)
        => AdvanceTo(consumed, consumed);

    public override void AdvanceTo(SequencePosition consumed, SequencePosition examined)
    {
        if (_held.Count == 0)
            return;

        long consumedBytes = _lastSequence.Slice(0, consumed).Length;

        while (_held.Count > 0 && consumedBytes > 0)
        {
            var seg = _held[0];
            int available = seg.Memory.Length;

            if (consumedBytes >= available)
            {
                _inner.ReturnRing(seg.BufferId);
                _held.RemoveAt(0);
                consumedBytes -= available;
            }
            else
            {
                _held[0] = new HeldBuffer(seg.Memory.Slice((int)consumedBytes), seg.BufferId);
                consumedBytes = 0;
            }
        }
    }

    public override void CancelPendingRead()
        => _cancelRequested = true;

    public override void Complete(Exception? exception = null)
    {
        if (_completed)
            return;

        _completed = true;

        foreach (var seg in _held)
            _inner.ReturnRing(seg.BufferId);

        _held.Clear();
    }

    private void DrainSnapshot(RingSnapshot result)
    {
        var rings = _inner.GetAllSnapshotRingsAsUnmanagedMemory(result);
        foreach (var ring in rings)
            _held.Add(new HeldBuffer(ring.Memory, ring.BufferId));
    }

    private ReadOnlySequence<byte> BuildSequence()
    {
        if (_held.Count == 0)
        {
            _lastSequence = default;
            return _lastSequence;
        }

        if (_held.Count == 1)
        {
            _lastSequence = new ReadOnlySequence<byte>(_held[0].Memory);
            return _lastSequence;
        }

        var head = new RingSegment(_held[0].Memory, _held[0].BufferId);
        var tail = head;

        for (int i = 1; i < _held.Count; i++)
            tail = tail.Append(_held[i].Memory, _held[i].BufferId);

        _lastSequence = new ReadOnlySequence<byte>(head, 0, tail, tail.Memory.Length);
        return _lastSequence;
    }

    private void ThrowIfCompleted()
    {
        if (_completed)
            throw new InvalidOperationException(
                "Reading is not allowed after the reader was completed.");
    }

    private readonly record struct HeldBuffer(ReadOnlyMemory<byte> Memory, ushort BufferId);
}
