using System.Buffers;
using System.Threading.Tasks.Sources;
using ioxide.utils;

// ReSharper disable SuggestVarOrType_BuiltInTypes

namespace ioxide;

public sealed unsafe partial class Connection : IValueTaskSource, IBufferWriter<byte>
{
    private readonly int _writeSlabSize;
    internal byte* WriteBuffer;
    internal int   WriteHead;
    internal int   WriteTail;
    internal int   WriteInFlight;

    // Outstanding IORING_CQE_F_NOTIF completions for in-flight zero-copy sends. The slab can't be
    // recycled until this hits zero (the kernel still owns the buffer until the notif). Always 0 for
    // plain SEND; reset on Clear().
    internal int   ZcNotifPending;

    private readonly UnmanagedMemoryManager _manager;

    private ManualResetValueTaskSourceCore<bool> _flushSignal = new()
    {
        RunContinuationsAsynchronously = false,
    };
    private int _flushArmed;
    private int _flushInProgress;

#region IBufferWriter<byte>

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        if (Volatile.Read(ref _flushInProgress) != 0)
        {
            throw new InvalidOperationException("Cannot write while flush is in progress.");
        }

        int remaining = _writeSlabSize - WriteTail;
        if (sizeHint > remaining)
        {
            throw new InvalidOperationException("Buffer too small.");
        }

        return _manager.Memory.Slice(WriteTail, remaining);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        if (Volatile.Read(ref _flushInProgress) != 0)
        {
            throw new InvalidOperationException("Cannot write while flush is in progress.");
        }

        if (WriteTail + sizeHint > _writeSlabSize)
        {
            throw new InvalidOperationException("Write buffer too small.");
        }

        return new Span<byte>(WriteBuffer + WriteTail, _writeSlabSize - WriteTail);
    }

    public void Advance(int count)
    {
        if (Volatile.Read(ref _flushInProgress) != 0)
        {
            throw new InvalidOperationException("Cannot write while flush is in progress.");
        }

        WriteTail += count;
    }

#endregion

    public void Write(ReadOnlySpan<byte> source)
    {
        if (Volatile.Read(ref _flushInProgress) != 0)
        {
            throw new InvalidOperationException("Cannot write while flush is in progress.");
        }

        int len = source.Length;
        if (WriteTail + len > _writeSlabSize)
        {
            throw new InvalidOperationException("Write buffer too small.");
        }

        source.CopyTo(new Span<byte>(WriteBuffer + WriteTail, len));
        WriteTail += len;
    }

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
