using System.Runtime.CompilerServices;

namespace terraform;

public partial class Connection
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe void Write(ReadOnlyMemory<byte> source)
    {
        if (Volatile.Read(ref _flushInProgress) != 0)
            throw new InvalidOperationException("Cannot write while flush is in progress.");

        int len = source.Length;
        if (WriteTail + len > _writeSlabSize)
            throw new InvalidOperationException("Buffer too small.");

        source.Span.CopyTo(
            new Span<byte>(WriteBuffer + WriteTail, len)
        );

        WriteTail += len;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe void Write(ReadOnlySpan<byte> source)
    {
        if (Volatile.Read(ref _flushInProgress) != 0)
            throw new InvalidOperationException("Cannot write while flush is in progress.");

        int len = source.Length;
        if (WriteTail + len > _writeSlabSize)
            throw new InvalidOperationException("Buffer too small.");

        source.CopyTo(new Span<byte>(WriteBuffer + WriteTail, len));
        WriteTail += len;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask FlushAsync()
    {
        if (Interlocked.Exchange(ref _flushInProgress, 1) == 1)
            throw new InvalidOperationException("FlushAsync already in progress.");

        int target = WriteTail;

        if (target == 0)
        {
            Volatile.Write(ref _flushInProgress, 0);
            return default;
        }

        if (Interlocked.Exchange(ref _flushArmed, 1) == 1)
            throw new InvalidOperationException("FlushAsync already armed.");

        _flushSignal.Reset();

        WriteInFlight = target;

        Reactor.EnqueueFlush(ClientFd);

        return new ValueTask(this, token: 0);
    }
}
