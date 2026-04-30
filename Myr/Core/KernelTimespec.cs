namespace Myr.Core;

/// <summary>
/// Kernel timespec (16 bytes).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct KernelTimespec
{
    public long tv_sec;
    public long tv_nsec;
}