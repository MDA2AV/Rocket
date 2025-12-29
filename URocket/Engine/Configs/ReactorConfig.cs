namespace URocket.Engine.Configs;

public sealed record ReactorConfig(
    uint RingFlags = 0,
    int SqCpuThread = -1,
    uint SqThreadIdleMs = 100,
    uint RingEntries = 8 * 1024,
    int RecvBufferSize = 32 * 1024,
    int BufferRingEntries = 16 * 1024,
    int BatchCqes = 4096,
    int MaxConnectionsPerReactor = 8 * 1024);