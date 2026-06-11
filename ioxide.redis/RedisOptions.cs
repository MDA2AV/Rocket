namespace ioxide.redis;

/// <summary>How to reach Redis and how much capacity to provision per reactor.</summary>
public sealed record RedisOptions
{
    /// <summary>IPv4 literal. Resolve hostnames up front - DNS would block the reactor.</summary>
    public string Host { get; init; } = "127.0.0.1";

    public ushort Port { get; init; } = 6379;

    /// <summary>AUTH password (or null for no auth). With <see cref="User"/> set, uses ACL AUTH.</summary>
    public string? Password { get; init; }

    /// <summary>ACL username (Redis 6+); null uses the default user with <see cref="Password"/>.</summary>
    public string? User { get; init; }

    /// <summary>Logical database index to SELECT on connect.</summary>
    public int Database { get; init; }

    /// <summary>
    /// Connections opened per reactor. One connection carries one in-flight command (or pipeline)
    /// at a time, so this is the reactor's Redis concurrency; total = PoolSize × ReactorCount.
    /// </summary>
    public int PoolSize { get; init; } = 8;
}
