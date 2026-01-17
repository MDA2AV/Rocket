using System.Runtime.CompilerServices;

namespace URocket.Utils;

// Next steps
// TODO ReadResult should also contain a ReadOnlySequence<byte> by draining _recv everytime
// TODO need to think how to avoid allocating the data? Might not be possible.

// TODO Best scenario is drain, add to ReadOnlySequence<byte> and return the ring, everything transparent to the user

public readonly struct ReadResult
{
    public readonly long TailSnapshot;   // drain boundary
    public readonly bool IsClosed;      // socket closed / connection returned
    public readonly int Error;          // 0 or -errno (optional)

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadResult(long tailSnapshot, bool isClosed, int error = 0)
    {
        TailSnapshot = tailSnapshot;
        IsClosed     = isClosed;
        Error        = error;
    }

    public static ReadResult Closed(int error = 0) => new(0, true, error);
}