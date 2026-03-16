using System.Runtime.CompilerServices;

namespace terraform.Utils;

public readonly struct RingSnapshot
{
    public readonly long TailSnapshot;
    public readonly bool IsClosed;
    public readonly int Error;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RingSnapshot(long tailSnapshot, bool isClosed, int error = 0)
    {
        TailSnapshot = tailSnapshot;
        IsClosed     = isClosed;
        Error        = error;
    }

    public static RingSnapshot Closed(int error = 0) => new(0, true, error);
}
