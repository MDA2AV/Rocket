// ReSharper disable InvalidXmlDocComment
namespace URocket.Engine.Configs;

/// <summary>
/// Configuration for the acceptor io_uring instance.
/// The acceptor owns a dedicated ring used exclusively for multishot accept.
/// </summary>
public sealed record AcceptorConfig(
    /// <summary>
    /// io_uring setup flags (IORING_SETUP_*).
    ///
    /// Typical values:
    ///   - IORING_SETUP_SQPOLL : kernel thread polls the SQ to avoid syscalls.
    ///   - IORING_SETUP_SQ_AFF : pin SQPOLL thread to a specific CPU.
    ///
    /// For acceptor rings, SQPOLL is often unnecessary because
    /// accept CQEs are driven by network interrupts anyway.
    /// </summary>
    uint RingFlags = 0,
    //uint RingFlags = ABI.ABI.IORING_SETUP_SQPOLL | ABI.ABI.IORING_SETUP_SQ_AFF,

    /// <summary>
    /// CPU core to pin the SQPOLL kernel thread to.
    /// Only used when IORING_SETUP_SQPOLL is enabled.
    ///
    /// -1 means "no affinity / kernel decides".
    /// </summary>
    int SqCpuThread = -1,

    /// <summary>
    /// How long (in milliseconds) the SQPOLL kernel thread stays alive
    /// without submissions before sleeping.
    ///
    /// Only meaningful when IORING_SETUP_SQPOLL is enabled.
    /// </summary>
    uint SqThreadIdleMs = 100,

    /// <summary>
    /// Number of entries in the submission queue (SQ) and completion queue (CQ).
    ///
    /// For a multishot acceptor, this mainly bounds:
    ///   - the number of in-flight accept completions
    ///   - the maximum burst of incoming connections that can be queued
    ///
    /// This does NOT limit concurrent connections directly; it limits
    /// how many accept CQEs can be pending at once.
    /// </summary>
    uint RingEntries = 8 * 1024,

    /// <summary>
    /// Maximum number of CQEs processed per loop iteration when calling
    /// io_uring_peek_batch_cqe().
    ///
    /// This value should be <= RingEntries.
    /// Larger values reduce per-accept overhead during bursts but increase
    /// latency for other acceptor work.
    ///
    /// 
    /// BatchSqes controls how many accepted connections are processed per event-loop iteration.
    /// In the acceptor, each CQE represents a successful accept() completion, and handling one
    /// accept immediately generates follow-up work that places pressure on the submission queue
    /// (assigning the fd to a reactor, arming the first multishot recv, etc.). Although the value
    /// is used as the upper bound when batching CQEs, the real constraint it tunes is how much SQ-side
    /// work we allow per loop, not CQ capacity. For this reason the setting is named BatchSqes rather
    /// than BatchCqes: it reflects submission-queue pressure and connection handoff rate, not completion-queue throughput.
    /// </summary>
    uint BatchSqes = 4096
);
