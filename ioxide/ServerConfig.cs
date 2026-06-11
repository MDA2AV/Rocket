namespace ioxide;

/// <summary>All server tunables; override via object initializer.</summary>
public sealed record ServerConfig
{
    public ushort Port         { get; init; } = 8080;

    /// <summary>
    /// Additional listener ports (every reactor binds each one). Connections carry the port they
    /// arrived on in <see cref="Connection.ListenerPort"/>, so one handler can serve several
    /// entry points (e.g. plaintext + TLS).
    /// </summary>
    public ushort[] ExtraPorts { get; init; } = [];
    public int    ReactorCount { get; init; } = 12;

    // io_uring SQ/CQ depth.
    public uint   RingEntries  { get; init; } = 8192;

    // Shared buffer ring (Incremental == false).
    public int    RecvBufferSize    { get; init; } = 32 * 1024;
    public int    BufferRingEntries { get; init; } = 4096;

    // Per-connection write slab + connection pool cap.
    public int    WriteSlabSize { get; init; } = 16 * 1024;
    public int    PoolMax       { get; init; } = 1024;

    // Per-connection SPSC recv queue depth (power of two); overflow closes the connection.
    public int    RecvQueueEntries { get; init; } = 64;

    // Incremental mode (IOU_PBUF_RING_INC, kernel 6.12+) - per-connection rings.
    // Reserved native memory ≈ PoolMax × ConnBufRingEntries × IncRecvBufferSize × ReactorCount.
    public bool   Incremental        { get; init; } = false;
    public int    MaxConnections     { get; init; } = 4096;   // one bgid per active connection
    public int    ConnBufRingEntries { get; init; } = 16;
    public int    IncRecvBufferSize  { get; init; } = 4096;
}
