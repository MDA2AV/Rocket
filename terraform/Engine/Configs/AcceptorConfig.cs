namespace terraform.Engine.Configs;

public sealed record AcceptorConfig(
    uint RingFlags = 0,
    int SqCpuThread = -1,
    uint SqThreadIdleMs = 100,
    uint RingEntries = 8 * 1024,
    uint BatchSqes = 4096,
    long CqTimeout = 100_000_000,
    IPVersion IPVersion = IPVersion.IPv6DualStack
);
