namespace Myr.Core.IoUring;

/// <summary>
/// Buffer ring registration (40 bytes).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct IoUringBufReg
{
    public ulong ring_addr;
    public uint  ring_entries;
    public ushort bgid;
    public ushort flags;
    public ulong resv0;
    public ulong resv1;
    public ulong resv2;
}