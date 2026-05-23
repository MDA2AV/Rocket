using System.IO.Pipelines;

namespace Spring;

/// <summary>
/// Kestrel-mode input path. The reactor copies recv bytes into a real BCL
/// <see cref="Pipe"/> and Kestrel reads <c>InputPipe.Reader</c> — bypassing the
/// hand-rolled read IVTS, which can't take Kestrel's concurrent off-reactor
/// access. Output still uses the write slab + FlushAsync (the single-issuer
/// EnqueueFlush handoff to the reactor). Null on the raw path.
/// </summary>
public sealed unsafe partial class Connection
{
    internal Pipe? InputPipe;

    internal void InitInputPipe()
        => InputPipe = new Pipe(new PipeOptions(
            pauseWriterThreshold: 0,
            resumeWriterThreshold: 0,
            useSynchronizationContext: false));

    /// <summary>Reactor-thread: copy recv bytes into the pipe and publish.</summary>
    internal void FeedInput(byte* ptr, int len)
    {
        Span<byte> dst = InputPipe!.Writer.GetSpan(len);
        new ReadOnlySpan<byte>(ptr, len).CopyTo(dst);
        InputPipe.Writer.Advance(len);
        _ = InputPipe.Writer.FlushAsync();   // no backpressure → completes synchronously
    }

    /// <summary>Reactor-thread: signal EOF to Kestrel's reader.</summary>
    internal void CompleteInput(Exception? error = null)
        => InputPipe?.Writer.Complete(error);

    /// <summary>
    /// Resume read/flush continuations on the thread pool. Kestrel drives the
    /// connection off-reactor, so the reactor's CompleteFlush must NOT run
    /// Kestrel inline. Call before the first FlushAsync.
    /// </summary>
    public void UseAsyncContinuations()
    {
        _readSignal.RunContinuationsAsynchronously = true;
        _flushSignal.RunContinuationsAsynchronously = true;
    }
}
