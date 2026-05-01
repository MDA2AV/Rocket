using System.Buffers;
using dogrider.Protocol;
using zerg;
using zerg.core;

namespace dogrider.Server;

internal sealed class WebsocketConnection : IImperativeConnection
{
    private readonly Connection _conn;
    private readonly int _maxFrameSize;

    private UnmanagedMemoryManager[]? _heldRings;

    private byte[] _carryover;
    private int _carryoverLen;

    private bool _disposed;

    public WebsocketConnection(Connection conn, int maxFrameSize = 1024 * 1024)
    {
        _conn = conn;
        _maxFrameSize = maxFrameSize;
        _carryover = ArrayPool<byte>.Shared.Rent(8 * 1024);
        _carryoverLen = 0;
    }

    public async ValueTask<WebsocketFrame> ReadFrameAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            if (_carryoverLen > 0)
            {
                var seq = new ReadOnlySequence<byte>(_carryover, 0, _carryoverLen);
                var frame = Frame.Decode(seq, _maxFrameSize, out var consumed, out _);

                if (!frame.IsError(out var err))
                {
                    var consumedBytes = (int)seq.Slice(seq.Start, consumed).Length;
                    ShiftCarryover(consumedBytes);
                    return frame;
                }

                if (err.ErrorType != FrameErrorType.Incomplete)
                    return frame;
            }

            cancellationToken.ThrowIfCancellationRequested();

            var snapshot = await _conn.ReadAsync().ConfigureAwait(false);
            if (snapshot.IsClosed)
                return new WebsocketFrame(new FrameError("Connection closed", FrameErrorType.ConnectionClosed));

            var rings = _conn.GetAllSnapshotRingsAsUnmanagedMemory(snapshot);
            _heldRings = rings;

            var combined = BuildCombinedSequence(rings);
            var frame2 = Frame.Decode(combined, _maxFrameSize, out var consumed2, out _);

            if (!frame2.IsError(out var err2))
            {
                var remainder = combined.Slice(consumed2);
                ResetCarryoverFrom(remainder);
                ReleaseRings(rings);
                return frame2;
            }

            if (err2.ErrorType == FrameErrorType.Incomplete)
            {
                ResetCarryoverFrom(combined);
                ReleaseRings(rings);
                continue;
            }

            _carryoverLen = 0;
            ReleaseRings(rings);
            return frame2;
        }
    }

    public void WriteText(ReadOnlySpan<byte> utf8Payload, bool fin = true) => Frame.EncodeText(_conn, utf8Payload, fin);
    public void WriteText(string text, bool fin = true) => Frame.EncodeText(_conn, text, fin);
    public void WriteBinary(ReadOnlySpan<byte> payload, bool fin = true) => Frame.EncodeBinary(_conn, payload, fin);
    public void WritePong(ReadOnlySpan<byte> payload) => Frame.EncodePong(_conn, payload);
    public void WritePing(ReadOnlySpan<byte> payload = default) => Frame.EncodePing(_conn, payload);
    public void WriteClose(ushort statusCode = 1000, string? reason = null) => Frame.EncodeClose(_conn, statusCode, reason);

    public ValueTask FlushAsync() => _conn.FlushAsync();

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;

        if (_heldRings != null)
        {
            ReleaseRings(_heldRings);
            _heldRings = null;
        }

        if (_carryover.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_carryover);
            _carryover = [];
            _carryoverLen = 0;
        }

        return ValueTask.CompletedTask;
    }

    private ReadOnlySequence<byte> BuildCombinedSequence(UnmanagedMemoryManager[] rings)
    {
        if (_carryoverLen == 0)
        {
            if (rings.Length == 1)
                return new ReadOnlySequence<byte>(rings[0].Memory);

            var head = new SeqSegment(rings[0].Memory);
            var tail = head;
            for (int i = 1; i < rings.Length; i++)
                tail = tail.Append(rings[i].Memory);
            return new ReadOnlySequence<byte>(head, 0, tail, tail.Memory.Length);
        }

        var carryMem = new ReadOnlyMemory<byte>(_carryover, 0, _carryoverLen);
        var first = new SeqSegment(carryMem);
        var current = first;
        for (int i = 0; i < rings.Length; i++)
            current = current.Append(rings[i].Memory);
        return new ReadOnlySequence<byte>(first, 0, current, current.Memory.Length);
    }

    private void ReleaseRings(UnmanagedMemoryManager[] rings)
    {
        for (int i = 0; i < rings.Length; i++)
            _conn.ReturnRing(rings[i].BufferId);
        _heldRings = null;
        _conn.ResetRead();
    }

    private void ShiftCarryover(int consumedBytes)
    {
        if (consumedBytes >= _carryoverLen)
        {
            _carryoverLen = 0;
            return;
        }
        var remaining = _carryoverLen - consumedBytes;
        Buffer.BlockCopy(_carryover, consumedBytes, _carryover, 0, remaining);
        _carryoverLen = remaining;
    }

    private void ResetCarryoverFrom(in ReadOnlySequence<byte> seq)
    {
        var len = (int)seq.Length;
        EnsureCarryoverCapacity(len);
        if (len == 0)
        {
            _carryoverLen = 0;
            return;
        }
        seq.CopyTo(_carryover.AsSpan(0, len));
        _carryoverLen = len;
    }

    private void EnsureCarryoverCapacity(int needed)
    {
        if (_carryover.Length >= needed) return;
        var newSize = Math.Max(needed, _carryover.Length * 2);
        var bigger = ArrayPool<byte>.Shared.Rent(newSize);
        if (_carryoverLen > 0)
            Buffer.BlockCopy(_carryover, 0, bigger, 0, _carryoverLen);
        ArrayPool<byte>.Shared.Return(_carryover);
        _carryover = bigger;
    }

    private sealed class SeqSegment : ReadOnlySequenceSegment<byte>
    {
        public SeqSegment(ReadOnlyMemory<byte> mem) { Memory = mem; }

        public SeqSegment Append(ReadOnlyMemory<byte> mem)
        {
            var next = new SeqSegment(mem) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }
}
