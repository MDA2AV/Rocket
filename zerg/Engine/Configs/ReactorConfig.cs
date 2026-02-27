// ReSharper disable InvalidXmlDocComment
namespace zerg.Engine.Configs;
    
/// <summary>
/// Configuration for a single io_uring-based reactor.
/// Each reactor owns one io_uring instance, one buf-ring, and a set of connections.
/// </summary>
public sealed record ReactorConfig(
    /// <summary>
    /// io_uring setup flags (IORING_SETUP_*).
    /// Examples:
    ///   - IORING_SETUP_SQPOLL : kernel thread polls the SQ, reducing syscalls.
    ///   - IORING_SETUP_SQ_AFF : pin SQPOLL thread to a specific CPU.
    ///   - IORING_SETUP_IOPOLL : poll-based I/O for O_DIRECT (not typical for sockets).
    ///
    /// These flags directly affect how submissions are consumed by the kernel.
    /// </summary>
    uint RingFlags = ABI.ABI.IORING_SETUP_SINGLE_ISSUER | ABI.ABI.IORING_SETUP_DEFER_TASKRUN,
    //uint RingFlags = ABI.ABI.IORING_SETUP_SINGLE_ISSUER,
    //uint RingFlags = 0,
    //uint RingFlags = ABI.ABI.IORING_SETUP_SQPOLL | ABI.ABI.IORING_SETUP_SQ_AFF,

    /// <summary>
    /// CPU to which the SQPOLL kernel thread is pinned.
    /// Only meaningful when IORING_SETUP_SQPOLL is enabled.
    ///
    /// -1 means "let the kernel decide".
    /// </summary>
    int SqCpuThread = -1,

    /// <summary>
    /// How long (in milliseconds) the SQPOLL kernel thread stays alive
    /// without seeing new submissions before going to sleep.
    ///
    /// Smaller values reduce CPU usage but increase wake-up latency.
    /// Larger values keep latency low but burn CPU.
    /// Only used when IORING_SETUP_SQPOLL is enabled.
    /// </summary>
    uint SqThreadIdleMs = 100,

    /// <summary>
    /// Total number of entries in the io_uring submission queue (SQ) and
    /// completion queue (CQ).
    ///
    /// This is the *hard upper bound* on how many in-flight operations
    /// (recv, send, accept, cancel, etc.) the reactor can have at once.
    ///
    /// If you attempt to acquire more SQEs than this, io_uring_get_sqe()
    /// will return NULL.
    /// </summary>
    uint RingEntries = 8 * 1024,

    /// <summary>
    /// Size in bytes of each receive buffer provided to the kernel via buf-ring.
    ///
    /// Each multishot recv selects one of these buffers and writes up to this size.
    /// Larger values reduce syscalls for large payloads but increase memory usage.
    /// </summary>
    int RecvBufferSize = 32 * 1024,

    /// <summary>
    /// Number of buffers registered in the io_uring buffer ring (buf-ring).
    ///
    /// This is the maximum number of receive buffers that can be simultaneously
    /// "in flight" (owned by the kernel or handed to user space).
    ///
    /// Must be a power of two.
    /// </summary>
    int BufferRingEntries = 16 * 1024,

    /// <summary>
    /// Maximum number of CQEs the reactor will process in a single batch
    /// using io_uring_peek_batch_cqe().
    ///
    /// Larger values improve throughput under load by amortizing overhead,
    /// but increase per-loop latency.
    /// </summary>
    int BatchCqes = 4096,

    /// <summary>
    /// Upper bound on how many concurrent connections this reactor
    /// is allowed to manage.
    ///
    /// This should be <= RingEntries to avoid exhausting SQEs when
    /// arming multishot recv operations for new connections.
    /// </summary>
    int MaxConnectionsPerReactor = 8 * 1024,

    /// <summary>
    /// Timeout (in nanoseconds) passed to io_uring_wait_cqes().
    ///
    /// Acts as a low-latency sleep when no completions are available.
    /// Smaller values reduce tail latency but increase CPU usage.
    /// </summary>
    long CqTimeout = 1_000_000,

    /// <summary>
    /// Enable incremental buffer consumption (IOU_PBUF_RING_INC).
    ///
    /// When enabled, the kernel can partially consume provided buffers across
    /// multiple recv CQEs instead of consuming one entire buffer per CQE.
    /// A single buffer can serve multiple recvs, reducing buffer ring pressure.
    ///
    /// Requires Linux kernel 6.12+.
    /// </summary>
    bool IncrementalBufferConsumption = false
);
