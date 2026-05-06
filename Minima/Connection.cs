using System.Runtime.InteropServices;
using System.Threading.Tasks.Sources;
using static Minima.Native;

namespace Minima;

/// <summary>
/// Per-connection state. Owns the recv slab and the read-side rendezvous
/// (IValueTaskSource&lt;int&gt;) between the CQE dispatcher (producer) and the
/// async handler (consumer).
/// </summary>
internal sealed unsafe class Connection : IValueTaskSource<int>
{
    public byte* Buffer;
    private readonly Reactor _reactor;

    public Connection(Reactor reactor) { _reactor = reactor; }

    // Read-side rendezvous state.
    private ManualResetValueTaskSourceCore<int> _readSignal;  // RunContinuationsAsynchronously = false (default)
    private int _armed;     // 1 when handler is parked on _readSignal
    private int _pending;   // 1 when a result arrived while no one was armed
    private int _closed;    // 1 once recv returned <=0 or send failed
    private int _lastRes;   // recv length (or 0/-errno on close), set before publishing

    public ValueTask<int> ReadAsync()
    {
        if (_closed != 0)
            return new ValueTask<int>(0);

        if (_pending == 1)
        {
            _pending = 0;
            return new ValueTask<int>(_lastRes);
        }

        if (_armed == 1)
            throw new InvalidOperationException("ReadAsync already armed.");
        _armed = 1;

        return new ValueTask<int>(this, _readSignal.Version);
    }

    public void ResetRead()
    {
        _readSignal.Reset();
        if (_closed != 0)
            _pending = 1;
    }

    // Producer: called from Dispatch on a recv CQE. res is cqe.res.
    public void Complete(int res)
    {
        _lastRes = res;
        if (res <= 0)
            _closed = 1;

        if (_armed == 1)
        {
            _armed = 0;
            _readSignal.SetResult(res);
        }
        else
        {
            _pending = 1;
        }
    }

    // Producer: called from Dispatch on a send error to wake the handler.
    public void MarkClosed()
    {
        _closed = 1;
        _lastRes = 0;
        if (_armed == 1)
        {
            _armed = 0;
            _readSignal.SetResult(0);
        }
        else
        {
            _pending = 1;
        }
    }

    // Pointer-hiding wrappers so the (safe) handler can drive I/O without
    // entering an unsafe context.
    public void QueueRecv(int fd) => _reactor.SubmitRecv(fd, Buffer, Program.BufferSize);
    public void QueueSend(int fd, uint len) => _reactor.SubmitSend(fd, Buffer, len);
    public void QueueResponse(int fd) => _reactor.SubmitSend(fd, Program.s_responseBytes, (uint)Program.s_responseLen);

    public void Close(int fd)
    {
        if (Buffer != null)
        {
            NativeMemory.Free(Buffer);
            Buffer = null;
        }
        _reactor.Connections.Remove(fd);
        close(fd);
    }

    int IValueTaskSource<int>.GetResult(short token) => _readSignal.GetResult(token);
    
    ValueTaskSourceStatus IValueTaskSource<int>.GetStatus(short token) => _readSignal.GetStatus(token);
    
    void IValueTaskSource<int>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        => _readSignal.OnCompleted(continuation, state, token, flags);
}
