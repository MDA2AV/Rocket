namespace ioxide;

/// <summary>
/// How a connection's write buffer absorbs a response larger than <see cref="TcpOptions.WriteSlabSize"/>.
/// </summary>
public enum WriteOverflowStrategy
{
    /// <summary>Grow the single contiguous slab (realloc + copy); the flush stays one SEND.</summary>
    Grow,

    /// <summary>Chain pooled slabs and flush them with one vectored SENDMSG (no realloc copies).</summary>
    Segmented,
}

/// <summary>The TCP side of <see cref="ServerConfig"/>: listeners, buffer rings, and the write path.</summary>
public sealed record TcpOptions
{
    public ushort Port { get; init; } = 8080;

    /// <summary>
    /// Additional listener ports (every reactor binds each one). Connections carry the port they
    /// arrived on in <see cref="TcpConnection.ListenerPort"/>, so one handler can serve several
    /// entry points (e.g. plaintext + TLS).
    /// </summary>
    public ushort[] ExtraPorts { get; init; } = [];

    /// <summary>listen() backlog per SO_REUSEPORT listener - the accept-queue depth for connection bursts.</summary>
    public int ListenBacklog { get; init; } = 1024;

    // Shared buffer ring (Incremental == false).
    public int RecvBufferSize    { get; init; } = 32 * 1024;
    public int BufferRingEntries { get; init; } = 4096;

    // Per-connection write slab + connection pool cap.
    public int WriteSlabSize { get; init; } = 16 * 1024;
    public int PoolMax       { get; init; } = 1024;

    // How a response larger than WriteSlabSize is buffered: grow the slab (default) or chain pooled
    // slabs flushed with one vectored SENDMSG.
    public WriteOverflowStrategy WriteOverflow { get; init; } = WriteOverflowStrategy.Grow;

    // Inject IORING_OP_SEND_ZC (zero-copy send) for the response path instead of IORING_OP_SEND.
    // Trades the in-kernel payload copy for page-pinning plus a second (F_NOTIF) completion per send,
    // so it only pays off for large responses - leave off for small-payload workloads. The sender is
    // chosen once per connection at accept; kTLS connections always fall back to plain SEND (the
    // kernel re-buffers to encrypt, so zero-copy buys nothing there).
    public bool ZeroCopySend { get; init; } = false;

    // Per-connection SPSC recv queue depth (power of two); overflow closes the connection.
    public int RecvQueueEntries { get; init; } = 64;

    // Incremental mode (IOU_PBUF_RING_INC, kernel 6.12+) - per-connection rings.
    // Reserved native memory ≈ PoolMax × ConnBufRingEntries × IncRecvBufferSize × ReactorCount.
    public bool Incremental        { get; init; } = false;
    public int  MaxConnections     { get; init; } = 4096;   // one bgid per active connection
    public int  ConnBufRingEntries { get; init; } = 16;
    public int  IncRecvBufferSize  { get; init; } = 4096;
}
