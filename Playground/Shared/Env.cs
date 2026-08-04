namespace Playground.Shared;

/// <summary>
/// Typed reads for the PLAYGROUND_* environment knobs. Every sample configures itself this way, so
/// the "parse it or fall back" shape is written once instead of at each call site.
/// </summary>
public static class Env
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
