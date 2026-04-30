namespace Myr.Core.IoUring;

/// <summary>
/// Buffer ring entry (16 bytes).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct IoUringBuffer
{
    public ulong  addr;
    public uint   len;
    public ushort bid;
    public ushort resv;
}