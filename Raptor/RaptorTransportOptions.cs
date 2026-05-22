namespace Raptor;

public sealed class RaptorTransportOptions
{
    /// <summary>Number of reactor threads (each owns an io_uring + SO_REUSEPORT listener).</summary>
    public int ReactorCount { get; set; } = Math.Max(1, Environment.ProcessorCount);

    /// <summary>io_uring SQ/CQ depth per ring.</summary>
    public uint RingEntries { get; set; } = 8192;

    /// <summary>Per-connection recv buffer size (bytes).</summary>
    public int RecvBufferSize { get; set; } = 16 * 1024;

    /// <summary>listen() backlog.</summary>
    public int Backlog { get; set; } = 65535;
}
