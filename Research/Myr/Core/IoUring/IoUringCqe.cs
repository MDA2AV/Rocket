namespace Myr.Core.IoUring;

/// <summary>
/// Completion Queue Entry (16 bytes).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct IoUringCqe
{
    public ulong user_data;
    public int   res;
    public uint  flags;
}