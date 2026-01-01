using System.Runtime.CompilerServices;

namespace URocket.Utils;


public readonly struct ReadResult
{
    public readonly int TailSnapshot;   // drain boundary
    public readonly bool IsClosed;      // socket closed / connection returned
    public readonly int Error;          // 0 or -errno (optional)

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadResult(int tailSnapshot, bool isClosed, int error = 0)
    {
        TailSnapshot = tailSnapshot;
        IsClosed     = isClosed;
        Error        = error;
    }

    public static ReadResult Closed(int error = 0) => new(0, true, error);
}