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

    /// <summary>
    /// Apply the harness overrides to a sample's knobs, in one call, so the sample itself can
    /// declare them as plain literals you edit.
    ///
    /// The knobs are the sample's real configuration; this exists only because bench/run.sh and
    /// the Dockerfile drive every sample from the outside. Delete the call when you copy a sample
    /// out - the literals above it are the whole configuration and nothing else reads these.
    /// </summary>
    public static void Override(ref ushort port, ref int reactors)
    {
        port = Port("PLAYGROUND_PORT", port);
        reactors = Int("PLAYGROUND_REACTORS", reactors);
    }

    /// <inheritdoc cref="Override(ref ushort, ref int)"/>
    public static void Override(ref ushort port, ref int reactors, ref int bodyBytes)
    {
        Override(ref port, ref reactors);
        bodyBytes = Int("PLAYGROUND_BODY", bodyBytes);
    }
}
