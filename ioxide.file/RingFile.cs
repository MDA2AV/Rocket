using System.Threading.Tasks.Sources;
using static ioxide.file.Native;

namespace ioxide.file;

/// <summary>
/// A file whose reads run on a host io_uring reactor.
///
/// The file is opened once with a blocking syscall. Each read is an IORING_OP_READ at an explicit
/// offset, submitted on the host's ring and awaited through this object's value-task source — so
/// the read resumes inline on the reactor thread, and the reactor is free to serve other work while
/// the kernel fetches the pages.
///
/// This is what lets an HTTP handler serve a file straight off the ring (the Fractal model), rather
/// than handing file I/O to a separate thread pool the way epoll-based stacks must.
///
/// One in-flight read at a time per <see cref="RingFile"/>.
/// </summary>
public sealed class RingFile : IRingCompletion, IValueTaskSource<int>
{
    private readonly IRingHost _host;

    private ManualResetValueTaskSourceCore<int> _completion = new()
    {
        RunContinuationsAsynchronously = false
    };

    public int Fd { get; }

    public long Length { get; }

    private RingFile(IRingHost host, int fd, long length)
    {
        _host = host;
        Fd = fd;
        Length = length;
    }

    /// <summary>
    /// Open <paramref name="path"/> read-only and bind it to <paramref name="host"/> so that its
    /// read completions route here. Blocking open, one-time.
    /// </summary>
    public static RingFile Open(IRingHost host, string path)
    {
        int fd = open(path, O_RDONLY, 0);

        if (fd < 0)
            throw new InvalidOperationException($"Could not open '{path}'.");

        long length = FileLength(fd);

        var file = new RingFile(host, fd, length);
        host.Bind(fd, file);

        return file;
    }

    /// <summary>
    /// Read up to <paramref name="length"/> bytes into <paramref name="destination"/>, starting at
    /// <paramref name="offset"/> in the file. The result is the number of bytes read, which is 0 at
    /// end of file.
    /// </summary>
    public ValueTask<int> ReadAsync(nint destination, int length, long offset)
    {
        _completion.Reset();

        var pending = new ValueTask<int>(this, _completion.Version);

        _host.SubmitRead(Fd, destination, length, offset);

        return pending;
    }

    // ── The reactor calls this when a READ on our fd completes ──────────────────────────────

    public void Complete(int result)
    {
        _completion.SetResult(result);
    }

    int IValueTaskSource<int>.GetResult(short token)
    {
        return _completion.GetResult(token);
    }

    ValueTaskSourceStatus IValueTaskSource<int>.GetStatus(short token)
    {
        return _completion.GetStatus(token);
    }

    void IValueTaskSource<int>.OnCompleted(
        Action<object?> continuation,
        object? state,
        short token,
        ValueTaskSourceOnCompletedFlags flags)
    {
        _completion.OnCompleted(continuation, state, token, flags);
    }
}
