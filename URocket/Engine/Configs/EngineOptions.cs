namespace URocket.Engine.Configs;

/// <summary>
/// Configuration for the URocket engine.
/// Defines network binding, acceptor behavior, and reactor topology.
/// </summary>
public class EngineOptions
{
    /// <summary>
    /// Number of reactor threads (event loops) to spawn.
    /// Each reactor owns its own io_uring instance and connection set.
    /// </summary>
    public int ReactorCount { get; init; } = 1;

    /// <summary>
    /// IP address to bind the listening socket to.
    /// For dual-stack mode, IPv4 literals are mapped to IPv4-mapped IPv6 addresses.
    /// Use "0.0.0.0" or "::" to bind all interfaces.
    /// </summary>
    public string Ip { get; init; } = "0.0.0.0";

    /// <summary>
    /// TCP port to listen on.
    /// </summary>
    public ushort Port { get; init; } = 8080;

    /// <summary>
    /// Listen backlog passed to listen().
    /// Controls how many pending connections the kernel may queue.
    /// </summary>
    public int Backlog { get; init; } = 65535;

    /// <summary>
    /// Configuration for the acceptor ring and its event loop.
    /// </summary>
    public AcceptorConfig AcceptorConfig { get; init; } = new();

    /// <summary>
    /// Per-reactor configuration.
    /// Must contain at least ReactorCount entries.
    /// Each reactor uses the config at its index.
    /// </summary>
    public ReactorConfig[] ReactorConfigs { get; set; } = null!;
}