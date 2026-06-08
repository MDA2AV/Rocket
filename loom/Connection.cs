using System.Runtime.InteropServices;
using System.Threading.Tasks.Sources;

namespace Loom;

/// <summary>Per-connection state. Owned by one reactor thread; native (off-GC) buffers.</summary>
internal sealed unsafe class Connection : IValueTaskSource<int>
{
    public const int RecvBuf = 8 * 1024;
    public const int WriteBuf = 16 * 1024;

    public int Fd;
    public byte* Recv;
    public int RecvLen;
    public byte* Write;
    public int WriteLen;
    public int WriteSent;
    public bool CloseAfter;

    // IVTS for an io_uring-native await: a handler can `await` a ring op, and the reactor
    // completes it from the matching CQE — RCA=false ⇒ the continuation resumes inline on the
    // reactor thread, with NO thread pool involved.
    private ManualResetValueTaskSourceCore<int> _ring = new() { RunContinuationsAsynchronously = false };

    public ValueTask<int> RingAwait()
    {
        _ring.Reset();
        return new ValueTask<int>(this, _ring.Version);
    }

    public void RingComplete(int res) => _ring.SetResult(res);

    int IValueTaskSource<int>.GetResult(short token) => _ring.GetResult(token);
    ValueTaskSourceStatus IValueTaskSource<int>.GetStatus(short token) => _ring.GetStatus(token);
    void IValueTaskSource<int>.OnCompleted(Action<object?> c, object? s, short token, ValueTaskSourceOnCompletedFlags f)
        => _ring.OnCompleted(c, s, token, f);

    public Connection()
    {
        Recv = (byte*)NativeMemory.Alloc(RecvBuf);
        Write = (byte*)NativeMemory.Alloc(WriteBuf);
    }

    public void Reset(int fd)
    {
        Fd = fd; RecvLen = 0; WriteLen = 0; WriteSent = 0; CloseAfter = false;
    }

    public void FreeNative()
    {
        NativeMemory.Free(Recv);
        NativeMemory.Free(Write);
    }
}
