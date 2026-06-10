namespace Kingslayer;

/// <summary>
/// Engine tunables for a fleet of <see cref="Reactor"/>s, plus the per-connection <see cref="Handler"/>
/// each reactor starts on accept. Unlike Minima there is no incremental mode and no UsePipe/queue
/// tuning — Kingslayer is shared-buffer-ring only, and off-reactor work is handled by the per-reactor
/// <see cref="ReactorSynchronizationContext"/> rather than the return/flush/recycle MPSC queues.
/// </summary>
public sealed record ServerConfig
{
    public ushort Port         { get; init; } = 8080;
    public int    ReactorCount { get; init; } = Environment.ProcessorCount;

    /// <summary>Per-connection handler, started on accept while this reactor's SynchronizationContext
    /// is current, so every await (incl. <c>await Task.Run(...)</c>) resumes back on the reactor.</summary>
    public required Func<Reactor, Connection, Task> Handler { get; init; }

    public uint RingEntries       { get; init; } = 8192;
    public int  RecvBufferSize    { get; init; } = 32 * 1024;
    public int  BufferRingEntries { get; init; } = 4096;
    public int  WriteSlabSize     { get; init; } = 16 * 1024;
    public int  PoolMax           { get; init; } = 1024;
}
