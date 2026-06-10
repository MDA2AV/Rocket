namespace Myr.Core.IoUring;

/// <summary>
/// Extended argument for io_uring_enter with IORING_ENTER_EXT_ARG (24 bytes).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct IoUringGetEventsArg
{
    public ulong sigmask;
    public uint  sigmask_sz;
    public uint  pad;
    public ulong ts; // pointer to KernelTimespec, passed as u64
}