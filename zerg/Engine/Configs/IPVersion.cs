namespace zerg.Engine.Configs;

/// <summary>
/// Controls which IP stack the engine uses for its listening socket.
/// </summary>
public enum IPVersion
{
    /// <summary>
    /// Creates an IPv4-only listening socket (AF_INET).
    /// Only IPv4 clients can connect.
    /// </summary>
    IPv4Only,

    /// <summary>
    /// Creates an IPv6 listening socket (AF_INET6) with IPV6_V6ONLY = 0,
    /// enabling dual-stack mode.
    ///
    /// This socket accepts:
    ///  - native IPv6 connections
    ///  - IPv4 connections via IPv4-mapped IPv6 addresses (::ffff:a.b.c.d)
    /// </summary>
    IPv6DualStack
}
