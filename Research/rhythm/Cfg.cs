namespace Rhythm;

/// <summary>Server tunables in one place.</summary>
internal static class Cfg
{
    /// io_uring SQ depth (per reactor).
    public const uint RingEntries = 4096;

    /// Highest accepted fd: bounds the O(1) fd→connection slot table. Linux hands
    /// out the lowest free fd per accept, so per-reactor fds stay well below this.
    public const int MaxFd = 1 << 16;

    /// Per-connection receive buffer — accumulates partial/pipelined requests
    /// and is parsed in place.
    public const int RecvBuf = 8 * 1024;

    /// Per-connection write buffer — responses are serialized straight into it.
    /// Holds the largest single response (json/50 ≈ 10 KB) or a pipelined batch.
    public const int WriteBuf = 16 * 1024;

    /// Connection pool cap per reactor (reused across the limited-conn churn).
    public const int PoolMax = 4096;

    /// send() flag: suppress SIGPIPE if the peer is gone.
    public const uint MsgNoSignal = 0x4000;
}
