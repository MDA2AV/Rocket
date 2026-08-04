using ioxide;
using ioxide.file;
using ioxide.http11;
using ioxide.pg;

namespace Playground;

/// <summary>
/// Typed reads for the PLAYGROUND_* environment knobs. Every switch goes through here, so the
/// "parse it or fall back" shape is written once instead of at each call site.
/// </summary>
internal static class Env
{
    public static string Str(string name, string fallback)
        => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;

    public static string? StrOrNull(string name)
        => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

    public static int Int(string name, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out int value) ? value : fallback;

    public static long Long(string name, long fallback)
        => long.TryParse(Environment.GetEnvironmentVariable(name), out long value) ? value : fallback;

    public static ushort Port(string name, ushort fallback)
        => ushort.TryParse(Environment.GetEnvironmentVariable(name), out ushort value) ? value : fallback;

    public static bool Flag(string name)
        => Environment.GetEnvironmentVariable(name) == "1";
}

/// <summary>
/// Everything the Playground reads from the environment, resolved once at startup. Handlers take
/// their settings from here rather than calling <see cref="Environment"/> from static initializers,
/// so the full set of knobs is visible in one place (and documented in Playground/README.md).
/// </summary>
internal sealed class PlaygroundConfig
{
    public required string ModeName { get; init; }

    // Engine
    public required int Reactors { get; init; }
    public required ushort TcpPort { get; init; }
    public required int UdpSlots { get; init; }
    public required bool Incremental { get; init; }

    // raw / pipe / hop / taskrun: body size for the fixed response.
    public required int RawBodyBytes { get; init; }

    // file
    public required string AssetDir { get; init; }
    public required int AssetCacheMaxBytes { get; init; }

    // quic / h3 / h3-buffered / http3
    public required ushort QuicPort { get; init; }
    public required long QpackCapacity { get; init; }
    public required string? QuicCertPath { get; init; }
    public required string? QuicKeyPath { get; init; }

    // pg
    public required PgOptions Pg { get; init; }

    // proxy
    public required HttpClientOptions Upstream { get; init; }

    public static PlaygroundConfig FromEnvironment() => new()
    {
        ModeName = Env.Str("PLAYGROUND_MODE", "raw"),

        Reactors = Env.Int("PLAYGROUND_REACTORS", 12),
        TcpPort = Env.Port("PLAYGROUND_PORT", 8080),
        UdpSlots = Env.Int("PLAYGROUND_UDP_SLOTS", 16),
        Incremental = Env.Flag("PLAYGROUND_INCREMENTAL"),

        // 2 bytes is "ok"; a larger value serves an n-byte body so the raw handler can be compared
        // against other servers on the object size they conventionally measure (1024 B).
        // Non-positive values fall back to 2, matching the original parse.
        RawBodyBytes = Env.Int("PLAYGROUND_BODY", 2) is var body && body > 0 ? body : 2,

        AssetDir = Env.Str("PLAYGROUND_DIR", "/tmp/ioxide-assets"),
        // Per-file byte ceiling for pinning bodies in memory (0 forces every request through the
        // ring-read path).
        AssetCacheMaxBytes = Env.Int("PLAYGROUND_CACHE_MAX", AssetCache.DefaultMaxCachedFileBytes),

        QuicPort = Env.Port("PLAYGROUND_QUIC_PORT", 8443),
        // 4096 advertises a decode-side QPACK dynamic table (blocked streams 100); 0 = static-only,
        // nghttp3's default.
        QpackCapacity = Env.Long("PLAYGROUND_QPACK_CAP", 0),
        QuicCertPath = Env.StrOrNull("PLAYGROUND_QUIC_CERT"),
        QuicKeyPath = Env.StrOrNull("PLAYGROUND_QUIC_KEY"),

        Pg = new PgOptions
        {
            Host = Env.Str("PLAYGROUND_PG_HOST", "127.0.0.1"),
            Port = Env.Port("PLAYGROUND_PG_PORT", 5432),
            User = Env.Str("PLAYGROUND_PG_USER", "bench"),
            Database = Env.Str("PLAYGROUND_PG_DB", "bench"),
            PoolSize = Env.Int("PLAYGROUND_PG_POOL", 4),
            CommandTimeoutMs = Env.Int("PLAYGROUND_PG_TIMEOUT", 30_000),
        },

        Upstream = new HttpClientOptions
        {
            Host = Env.Str("PLAYGROUND_UPSTREAM_HOST", "127.0.0.1"),
            Port = Env.Port("PLAYGROUND_UPSTREAM_PORT", 8081),
            PoolSize = Env.Int("PLAYGROUND_UPSTREAM_POOL", 8),
        },
    };

    /// <summary>The engine config the reactors are built from.</summary>
    public ServerConfig ToServerConfig(QuicOptions? quic) => new()
    {
        ReactorCount = Reactors,
        Incremental = Incremental ? new IncrementalOptions() : null,
        Tcp = new TcpOptions { Port = TcpPort },
        Udp = new UdpOptions { RecvSlots = UdpSlots },
        Quic = quic,
    };
}
