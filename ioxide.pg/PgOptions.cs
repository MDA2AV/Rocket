namespace ioxide.pg;

/// <summary>
/// How to reach Postgres and how much capacity to provision per reactor.
/// </summary>
public sealed record PgOptions
{
    /// <summary>IPv4 literal. Resolve hostnames up front - DNS would block the reactor.</summary>
    public string Host { get; init; } = "127.0.0.1";

    public ushort Port { get; init; } = 5432;

    public required string User { get; init; }

    public required string Database { get; init; }

    /// <summary>Password for SCRAM-SHA-256; empty/null means trust auth.</summary>
    public string? Password { get; init; }

    /// <summary>
    /// Connections opened per reactor. One connection carries one query at a time, so this is the
    /// reactor's query concurrency; total server-side connections = PoolSize × ReactorCount.
    /// </summary>
    public int PoolSize { get; init; } = 8;
}
