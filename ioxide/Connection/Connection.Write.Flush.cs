using System.Threading.Tasks.Sources;

// ReSharper disable SuggestVarOrType_BuiltInTypes

namespace ioxide;

/// <summary>
/// The connection's flush lifecycle: hand the buffered response to the reactor for sending and park the
/// caller on an <see cref="IValueTaskSource"/> until that send completes. The reactor chooses a plain
/// SEND for a contiguous response or a vectored SENDMSG for a segmented one (Connection.Write.Segmented.cs).
/// </summary>
public sealed unsafe partial class Connection : IValueTaskSource
{
    private ManualResetValueTaskSourceCore<bool> _flushSignal = new()
    {
        RunContinuationsAsynchronously = false,
    };
    private int _flushArmed;
    private int _flushInProgress;

    public ValueTask FlushAsync()
    {
        // Connection already torn down: complete immediately so the handler unwinds
        // to its next ReadAsync, sees IsClosed, and exits.
        if (Volatile.Read(ref _closed) == 1)
        {
            return default;
        }

        if (Interlocked.Exchange(ref _flushInProgress, 1) == 1)
        {
            throw new InvalidOperationException("FlushAsync already in progress.");
        }

        int target = WriteTail;
        for (int i = 0; i < _ovCount; i++)
        {
            target += _ov![i].Used;
        }
        if (target == 0)
        {
            Volatile.Write(ref _flushInProgress, 0);

            return default;
        }

        if (Interlocked.Exchange(ref _flushArmed, 1) == 1)
        {
            throw new InvalidOperationException("FlushAsync already armed.");
        }

        _flushSignal.Reset();
        WriteInFlight = target;

        // A segmented response that spilled past the primary slab is gathered into one SENDMSG.
        _flushVectored = _inOverflow;
        if (_flushVectored)
        {
            BuildIovec();
        }

        // The generation lets the reactor drop a flush whose connection closed (or
        // whose fd was reused) before the queue drained.
        int gen = Volatile.Read(ref _generation);

        _reactor.EnqueueFlush(ClientFd, gen);

        // Race recovery: if close raced in after the entry guard, self-complete so we
        // don't hang on a send the reactor will never make.
        if (Volatile.Read(ref _closed) == 1 && Interlocked.Exchange(ref _flushArmed, 0) == 1)
        {
            Volatile.Write(ref _flushInProgress, 0);
            _flushSignal.SetResult(true);
        }

        return new ValueTask(this, (short)gen);
    }

    // Called by the reactor's send-completion path.
    internal void CompleteFlush()
    {
        if (_ovCount > 0)
        {
            ReleaseOverflow();
        }

        WriteHead = 0;
        WriteTail = 0;
        WriteInFlight = 0;
        ZcNotifPending = 0;
        Volatile.Write(ref _flushInProgress, 0);
        Interlocked.Exchange(ref _flushArmed, 0);

        _flushSignal.SetResult(true);
    }

#region IValueTaskSource

    void IValueTaskSource.GetResult(short token)
    {
        if (token != (short)Volatile.Read(ref _generation))
        {
            return;
        }

        _flushSignal.GetResult(_flushSignal.Version);
    }

    ValueTaskSourceStatus IValueTaskSource.GetStatus(short token)
    {
        if (token != (short)Volatile.Read(ref _generation))
        {
            return ValueTaskSourceStatus.Succeeded;
        }

        return _flushSignal.GetStatus(_flushSignal.Version);
    }

    void IValueTaskSource.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
    {
        if (token != (short)Volatile.Read(ref _generation))
        {
            continuation(state);

            return;
        }
        _flushSignal.OnCompleted(continuation, state, _flushSignal.Version, flags);
    }

#endregion
}
