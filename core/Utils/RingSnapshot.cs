using System.Runtime.CompilerServices;

namespace Zerg.Core;

public readonly struct RingSnapshot
{
    public readonly long TailSnapshot;   // drain boundary
    public readonly bool IsClosed;      // socket closed / connection returned
    public readonly int Error;          // 0 or -errno (optional)

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RingSnapshot(long tailSnapshot, bool isClosed, int error = 0)
    {
        TailSnapshot = tailSnapshot;
        IsClosed     = isClosed;
        Error        = error;
    }

    public static RingSnapshot Closed(int error = 0) => new(0, true, error);
}
