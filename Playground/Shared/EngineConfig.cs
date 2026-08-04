using ioxide;

namespace Playground.Shared;

/// <summary>
/// The engine knobs every sample shares, read from the environment. Sample-specific settings (a
/// Postgres host, an upstream port) are read by the sample itself, next to where they are used.
/// </summary>
public static class EngineConfig
{
    public static ServerConfig FromEnvironment(QuicOptions? quic = null) => new()
    {
        ReactorCount = Env.Int("PLAYGROUND_REACTORS", 12),
        Incremental = Env.Flag("PLAYGROUND_INCREMENTAL") ? new IncrementalOptions() : null,
        Tcp = new TcpOptions { Port = Env.Port("PLAYGROUND_PORT", 8080) },
        Udp = new UdpOptions { RecvSlots = Env.Int("PLAYGROUND_UDP_SLOTS", 16) },
        Quic = quic,
    };
}
