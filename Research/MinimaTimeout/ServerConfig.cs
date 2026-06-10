namespace MinimaTimeout;

/// <summary>
/// All server tunables in one place — replaces the consts that used to be
/// scattered across Program.cs and Reactor.cs. Defaults match the previous
/// hardcoded values; override via object initializer in Main, e.g.:
///   new ServerConfig { Port = 9000, ReactorCount = 8, Incremental = true }.
/// </summary>
public sealed record ServerConfig
{
    // Server-level.
    public ushort Port         { get; init; } = 8080;
    public int    ReactorCount { get; init; } = 12;

    // Handler style: false = raw ReadAsync/TryGetItem loop; true = PipeReader/PipeWriter.
    public bool   UsePipe      { get; init; } = false;

    // io_uring SQ/CQ depth.
    public uint   RingEntries  { get; init; } = 8192;

    // Reactor wait timeout in nanoseconds (zerg-style timed wait instead of an
    // eventfd wake). Too large hurts latency under low load; too small burns CPU
    // waking for nothing. 1ms matches zerg's reactor default.
    public long   CqTimeout    { get; init; } = 1_000; // <----

    // Shared buffer ring (used when Incremental == false).
    public int    RecvBufferSize    { get; init; } = 32 * 1024;
    public int    BufferRingEntries { get; init; } = 4096;

    // Per-connection write slab + connection pool cap.
    public int    WriteSlabSize { get; init; } = 16 * 1024;
    public int    PoolMax       { get; init; } = 1024;

    // Incremental mode (IOU_PBUF_RING_INC) — per-connection rings.
    //   reserved native memory ≈ PoolMax × ConnBufRingEntries × IncRecvBufferSize × ReactorCount.
    public bool   Incremental        { get; init; } = false;
    public int    MaxConnections     { get; init; } = 4096;   // GID cap (one bgid per active connection)
    public int    ConnBufRingEntries { get; init; } = 16;     // buffers per connection ring
    public int    IncRecvBufferSize  { get; init; } = 4096;   // bytes per buffer (filled incrementally)
}
