namespace rtr.Engine.Configs;

/// <summary>
/// Configuration for the rtr engine.
/// Defines network binding and reactor topology.
/// Each reactor binds its own SO_REUSEPORT listener and accepts directly on its own io_uring.
/// </summary>
public class EngineOptions
{
    /// <summary>
    /// Number of reactor threads (event loops) to spawn.
    /// Each reactor owns its own io_uring instance, listening socket, and connection set.
    /// </summary>
    public int ReactorCount { get; init; } = 1;

    /// <summary>
    /// IP address to bind every reactor's listening socket to.
    /// All reactors bind the SAME address with SO_REUSEPORT; the kernel hashes incoming SYNs
    /// across them.
    /// For dual-stack mode, IPv4 literals are mapped to IPv4-mapped IPv6 addresses.
    /// Use "0.0.0.0" or "::" to bind all interfaces.
    /// </summary>
    public string Ip { get; init; } = "0.0.0.0";

    /// <summary>
    /// TCP port to listen on. All reactors share this port via SO_REUSEPORT.
    /// </summary>
    public ushort Port { get; init; } = 8080;

    /// <summary>
    /// Listen backlog passed to listen() on each reactor's socket.
    /// </summary>
    public int Backlog { get; init; } = 65535;

    /// <summary>
    /// Controls which IP stack each reactor's listening socket uses.
    /// </summary>
    public IPVersion IPVersion { get; init; } = IPVersion.IPv6DualStack;

    /// <summary>
    /// Per-reactor configuration.
    /// Must contain at least ReactorCount entries.
    /// Each reactor uses the config at its index.
    /// </summary>
    public ReactorConfig[] ReactorConfigs { get; set; } = null!;
}
