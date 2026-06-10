using System.Threading.Tasks.Sources;

namespace ioxide.file;

/// <summary>
/// A per-reactor reader for positional file reads over the ring. Where <see cref="RingFile"/> is
/// bound to one file, a reader serves <em>any</em> descriptor — typically the shared descriptors an
/// <see cref="AssetCache"/> holds. Each read is an IORING_OP_READ at an offset, completed by the
/// reactor and resumed inline.
///
/// One read is in flight at a time per reader (one reader per reactor). Because the cache's reads
/// are positional and its descriptors are shared, different reactors read the same asset
/// concurrently on their own rings without interfering.
/// </summary>
public sealed class RingReader : IRingCompletion, IValueTaskSource<int>
{
    private readonly IRingHost _host;
    private ManualResetValueTaskSourceCore<int> _completion = new() { RunContinuationsAsynchronously = false };

    public RingReader(IRingHost host) => _host = host;

    /// <summary>
    /// Read up to <paramref name="length"/> bytes from <paramref name="fd"/> at
    /// <paramref name="offset"/> into <paramref name="destination"/>; returns the bytes read.
    /// </summary>
    public ValueTask<int> ReadAsync(int fd, nint destination, int length, long offset)
    {
        // Route this descriptor's completion to us, then submit the read. (Re-binding per read is
        // harmless — it's always the same reader on this reactor.)
        _host.Bind(fd, this);
        _completion.Reset();

        var pending = new ValueTask<int>(this, _completion.Version);
        _host.SubmitRead(fd, destination, length, offset);
        return pending;
    }

    public void Complete(int result) => _completion.SetResult(result);

    int IValueTaskSource<int>.GetResult(short token) => _completion.GetResult(token);
    ValueTaskSourceStatus IValueTaskSource<int>.GetStatus(short token) => _completion.GetStatus(token);
    void IValueTaskSource<int>.OnCompleted(Action<object?> c, object? s, short token, ValueTaskSourceOnCompletedFlags f)
        => _completion.OnCompleted(c, s, token, f);
}
