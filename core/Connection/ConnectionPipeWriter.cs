using System.IO.Pipelines;

namespace zerg.core;

public sealed class ConnectionPipeWriter : PipeWriter
{
    private readonly ConnectionBase _inner;
    private bool _completed;
    private bool _cancelRequested;
    private long _unflushed;

    public ConnectionPipeWriter(ConnectionBase inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public override bool CanGetUnflushedBytes => true;
    public override long UnflushedBytes => _unflushed;

    public override void Advance(int bytes)
    {
        _unflushed += bytes;
        _inner.Advance(bytes);
    }

    public override Memory<byte> GetMemory(int sizeHint = 0)
        => _inner.GetMemory(sizeHint);

    public override Span<byte> GetSpan(int sizeHint = 0)
        => _inner.GetSpan(sizeHint);

    public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
    {
        if (_cancelRequested)
        {
            _cancelRequested = false;
            return new ValueTask<FlushResult>(new FlushResult(isCanceled: true, isCompleted: _completed));
        }

        _unflushed = 0;
        ValueTask inner = _inner.FlushAsync();

        if (inner.IsCompletedSuccessfully)
            return new ValueTask<FlushResult>(new FlushResult(isCanceled: false, isCompleted: _completed));

        return AwaitFlush(inner);
    }

    private async ValueTask<FlushResult> AwaitFlush(ValueTask inner)
    {
        await inner;
        return new FlushResult(isCanceled: _cancelRequested, isCompleted: _completed);
    }

    public override void CancelPendingFlush()
        => _cancelRequested = true;

    public override void Complete(Exception? exception = null)
        => _completed = true;
}
