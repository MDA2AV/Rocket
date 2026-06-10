using zerg.Engine.Configs;

namespace KestrelZerg;

/// <summary>
/// Options for the zerg transport. The Ip/Port are taken from the Kestrel endpoint passed to
/// <see cref="ZergTransportFactory.BindAsync"/>; only reactor topology and io_uring tuning live here.
/// </summary>
public sealed class ZergTransportOptions
{
    /// <summary>
    /// Number of reactor threads. Each owns its own io_uring instance.
    /// </summary>
    public int ReactorCount { get; set; } = Math.Max(1, Environment.ProcessorCount / 2);

    /// <summary>
    /// IPv4-only or IPv6 dual-stack listening socket. Defaults to dual-stack.
    /// </summary>
    public IPVersion IPVersion { get; set; } = IPVersion.IPv6DualStack;

    /// <summary>
    /// Listen backlog passed to listen().
    /// </summary>
    public int Backlog { get; set; } = 65535;

    /// <summary>
    /// Optional per-reactor configuration. If null, defaults are used for every reactor.
    /// </summary>
    public ReactorConfig[]? ReactorConfigs { get; set; }
}
