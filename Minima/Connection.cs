using System.Threading.Tasks.Sources;
using static Minima.Native;

namespace Minima;

internal readonly struct RecvSnapshot
{
    public readonly long Tail;
    public readonly bool IsClosed;

    public RecvSnapshot(long tail, bool isClosed)
    {
        Tail = tail; 
        IsClosed = isClosed; 
    }
    
    public static RecvSnapshot Closed() => new(0, isClosed: true);
}

internal sealed unsafe class Connection : IValueTaskSource<RecvSnapshot>
{
    private readonly Reactor _reactor;

    public Connection(Reactor reactor)
    {
        _reactor = reactor; 
    }

    private ManualResetValueTaskSourceCore<RecvSnapshot> _readSignal;
    private int _armed;
    private int _closed;

    private readonly SpscRecvRing _recv = new(capacityPow2: 16);

    public ValueTask<RecvSnapshot> ReadAsync()
    {
        if (!_recv.IsEmpty())
        {
            return new ValueTask<RecvSnapshot>(new RecvSnapshot(_recv.SnapshotTail(), _closed != 0));
        }

        if (_closed != 0)
        {
            return new ValueTask<RecvSnapshot>(RecvSnapshot.Closed());
        }

        if (_armed == 1)
        {
            throw new InvalidOperationException("ReadAsync already armed.");
        }
        
        _armed = 1;

        return new ValueTask<RecvSnapshot>(this, _readSignal.Version);
    }

    public bool TryGetItem(in RecvSnapshot snap, out SpscRecvRing.Item item)
        => _recv.TryDequeueUntil(snap.Tail, out item);

    public void ResetRead() => _readSignal.Reset();

    public void Complete(int res, ushort bid, bool hasBuffer, byte* ptr)
    {
        if (res <= 0)
        {
            _closed = 1;
            if (hasBuffer)
            {
                _reactor.ReturnBuffer(bid);
            }
        }
        else if (!_recv.TryEnqueue(new SpscRecvRing.Item { Ptr = ptr, Bid = bid, Len = res, HasBuffer = hasBuffer }))
        {
            Console.Error.WriteLine("[conn] recv queue overflow; closing.");
            if (hasBuffer)
            {
                _reactor.ReturnBuffer(bid);
            }
            _closed = 1;
        }

        if (_armed == 1)
        {
            _armed = 0;
            _readSignal.SetResult(new RecvSnapshot(_recv.SnapshotTail(), _closed != 0));
        }
    }

    public void MarkClosed()
    {
        _closed = 1;
        
        if (_armed == 1)
        {
            _armed = 0;
            _readSignal.SetResult(new RecvSnapshot(_recv.SnapshotTail(), isClosed: true));
        }
    }

    public void QueueResponse(int fd) => _reactor.SubmitSend(fd, Program.s_responseBytes, (uint)Program.s_responseLen);

    public void Close(int fd)
    {
        while (_recv.TryDequeue(out SpscRecvRing.Item item))
        {
            if (item.HasBuffer)
            {
                _reactor.ReturnBuffer(item.Bid);
            }
        }
        
        _reactor.Connections.Remove(fd);
        close(fd);
    }

    RecvSnapshot IValueTaskSource<RecvSnapshot>.GetResult(short token) => _readSignal.GetResult(token);
    
    ValueTaskSourceStatus IValueTaskSource<RecvSnapshot>.GetStatus(short token) => _readSignal.GetStatus(token);
    
    void IValueTaskSource<RecvSnapshot>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        => _readSignal.OnCompleted(continuation, state, token, flags);
}
