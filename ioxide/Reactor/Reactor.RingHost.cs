using System.Runtime.CompilerServices;
using static ioxide.Native;

namespace ioxide;

/// <summary>
/// The reactor as an <see cref="IRingHost"/>.
///
/// A ring-native client — a Postgres connection, a file, a Redis socket — submits SEND/RECV/READ/
/// WRITE on its own descriptor through this reactor's ring, tagged <c>KindClient</c>. The matching
/// completion is routed back, in Dispatch, to whichever client bound that descriptor — inline, on
/// the reactor thread, with no .NET socket engine and no thread pool.
///
/// The engine deliberately knows nothing about the client types. A client only needs to be an
/// <see cref="IRingCompletion"/> and to have bound its descriptor.
/// </summary>
public sealed unsafe partial class Reactor : IRingHost
{
    private const ulong KindClient = 5UL << 32;

    // Which client to notify when a given descriptor's completion arrives.
    private readonly Dictionary<int, IRingCompletion> _ringTargets = new();

    /// <summary>
    /// A slot for the application to keep per-reactor state — typically the Postgres connection it
    /// opened from <see cref="OnStart"/>. The engine never reads it; it exists so a handler can
    /// reach its connection without the engine depending on any client type.
    /// </summary>
    public object? State;

    /// <summary>
    /// Runs on the reactor thread once the ring and listener are up, before the loop starts. The
    /// application opens its ring-native clients here, since they must be created (and bound) on
    /// this thread.
    /// </summary>
    public Action<Reactor>? OnStart;

    /// <summary>
    /// The per-connection handler the application supplies. The reactor invokes it once for each
    /// accepted connection; the handler then loops over reads and writes until the connection
    /// closes. This is the seam between the engine and the application's request logic.
    /// </summary>
    public Func<Reactor, Connection, Task> Handle = null!;

    public void Bind(int fd, IRingCompletion target)
    {
        _ringTargets[fd] = target;
    }

    public void SubmitSend(int fd, nint buffer, int length)
    {
        SubmitClientOp(IORING_OP_SEND, fd, buffer, length, offset: 0);
    }

    public void SubmitRecv(int fd, nint buffer, int length)
    {
        SubmitClientOp(IORING_OP_RECV, fd, buffer, length, offset: 0);
    }

    public void SubmitRead(int fd, nint buffer, int length, long offset)
    {
        SubmitClientOp(IORING_OP_READ, fd, buffer, length, offset);
    }

    public void SubmitWrite(int fd, nint buffer, int length, long offset)
    {
        SubmitClientOp(IORING_OP_WRITE, fd, buffer, length, offset);
    }

    private void SubmitClientOp(byte opcode, int fd, nint buffer, int length, long offset)
    {
        IoUringSqe* sqe = GetSqeOrFlush();
        Unsafe.InitBlockUnaligned(sqe, 0, 64);

        sqe->opcode    = opcode;
        sqe->fd        = fd;
        sqe->addr      = (ulong)buffer;
        sqe->len       = (uint)length;
        sqe->off       = (ulong)offset;            // ignored for SEND/RECV; the file position for READ/WRITE
        sqe->user_data = KindClient | (uint)fd;
    }

    // Called from Dispatch when a KindClient completion lands.
    private void CompleteClient(int fd, int result)
    {
        if (_ringTargets.TryGetValue(fd, out IRingCompletion? target))
            target.Complete(result);
    }
}
