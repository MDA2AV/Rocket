using System.Threading.Tasks.Sources;
using static Minima.Native;

namespace Minima;

/// <summary>
/// Per-connection state. Holds the read-side rendezvous (IValueTaskSource&lt;int&gt;)
/// between the CQE dispatcher (producer) and the async handler (consumer).
/// Recv buffers come from the reactor's shared kernel buffer ring; Connection
/// just remembers the bid of the last delivered buffer so ResetRead can return it.
/// </summary>
internal sealed unsafe class Connection : IValueTaskSource<int>
{
    private readonly Reactor _reactor;

    public Connection(Reactor reactor) { _reactor = reactor; }

    // Read-side rendezvous state.
    private ManualResetValueTaskSourceCore<int> _readSignal;  // RunContinuationsAsynchronously = false (default)
    private int    _armed;       // 1 when handler is parked on _readSignal
    private int    _pending;     // 1 when a result arrived while no one was armed
    private int    _closed;      // 1 once recv returned <=0 or send failed
    private int    _lastRes;     // recv length (or 0/-errno on close)
    private ushort _bid;         // buffer id from the last recv CQE (only valid if _hasBuffer)
    private bool   _hasBuffer;   // true when a buffer is checked out and awaits return

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
        if (_hasBuffer)
        {
            _reactor.ReturnBuffer(_bid);
            _hasBuffer = false;
        }
        _readSignal.Reset();
        if (_closed != 0)
            _pending = 1;
    }

    // Producer: called from Dispatch on a recv CQE. res is cqe.res.
    public void Complete(int res, ushort bid, bool hasBuffer)
    {
        _lastRes = res;
        _bid = bid;
        _hasBuffer = hasBuffer;
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

    public void QueueResponse(int fd) => _reactor.SubmitSend(fd, Program.s_responseBytes, (uint)Program.s_responseLen);

    public void Close(int fd)
    {
        if (_hasBuffer)
        {
            _reactor.ReturnBuffer(_bid);
            _hasBuffer = false;
        }
        _reactor.Connections.Remove(fd);
        close(fd);
    }

    int IValueTaskSource<int>.GetResult(short token) => _readSignal.GetResult(token);

    ValueTaskSourceStatus IValueTaskSource<int>.GetStatus(short token) => _readSignal.GetStatus(token);

    void IValueTaskSource<int>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        => _readSignal.OnCompleted(continuation, state, token, flags);
}
