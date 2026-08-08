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
    /// Tri-state, unlike <see cref="Flag"/>: unset keeps the sample's own literal, so a harness can
    /// force a knob OFF as well as on. "1" and "0" are the only values that mean anything.
    /// </summary>
    public static bool Bool(string name, bool fallback)
        => Environment.GetEnvironmentVariable(name) switch
        {
            "1" => true,
            "0" => false,
            _ => fallback,
        };

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

    /// <summary>
    /// The QUIC form of <see cref="Override(ref ushort, ref int)"/>. A QUIC port is a UDP port, so
    /// it reads PLAYGROUND_QUIC_PORT and not PLAYGROUND_PORT - a sample that listens on both binds
    /// two different ports and would otherwise collide with itself.
    /// </summary>
    public static void OverrideQuic(ref ushort quicPort, ref int reactors)
    {
        quicPort = Port("PLAYGROUND_QUIC_PORT", quicPort);
        reactors = Int("PLAYGROUND_REACTORS", reactors);
    }

    /// <summary>
    /// The kTLS knobs, same escape hatch. These exist so bench/tls-matrix.sh can measure one build
    /// in every backend combination instead of rebuilding per cell - a sample that only ever runs
    /// one way does not need them.
    /// </summary>
    public static void OverrideKtls(ref bool kernelTx, ref bool kernelRx)
    {
        kernelTx = Bool("PLAYGROUND_KTLS_TX", kernelTx);
        kernelRx = Bool("PLAYGROUND_KTLS_RX", kernelRx);
    }

    /// <summary>
    /// For the samples where transmit is kTLS by construction and only receive is a choice.
    /// </summary>
    public static void OverrideKtls(ref bool kernelRx)
        => kernelRx = Bool("PLAYGROUND_KTLS_RX", kernelRx);
}
