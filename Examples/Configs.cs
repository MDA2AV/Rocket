using ioxide;

namespace Examples;

/// <summary>One config per buffer-ring mode, shared by both categories. 12 reactors each.</summary>
public static class Configs
{
    public static ServerConfig Shared => new()
    {
        ReactorCount = 12,
        RingEntries  = 8192,

        RecvBufferSize = 32 * 1024,
        RecvSlots      = 4096,

        Tcp = new TcpOptions
        {
            Port              = 8080,
            WriteSlabSize     = 16 * 1024,
            PoolMax           = 1024,
            RecvQueueEntries  = 64,
        },
    };

    public static ServerConfig Incremental => new()
    {
        ReactorCount = 12,
        RingEntries  = 8192,

        // RecvBufferSize / RecvSlots are shared-ring knobs - unused here; setting
        // Incremental is what selects the per-connection-ring mode.
        Incremental = new IncrementalOptions
        {
            MaxConnections = 4096,
            RecvSlots      = 16,
            RecvBufferSize = 4096,
        },

        Tcp = new TcpOptions
        {
            Port             = 8080,
            WriteSlabSize    = 16 * 1024,
            PoolMax          = 1024,
            RecvQueueEntries = 64,
        },
    };
}
