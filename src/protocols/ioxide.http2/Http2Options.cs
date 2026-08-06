namespace ioxide.http2;

/// <summary>Per-connection knobs for <see cref="Http2Connection"/>.</summary>
public sealed record Http2Options
{
    /// <summary>
    /// Ceiling on one request's headers plus body. A body arrives as a stream of DATA frames with
    /// no length known up front, so this is what bounds the arena a single stream can grow.
    /// </summary>
    public int MaxRequestBytes { get; init; } = 8 * 1024 * 1024;

    /// <summary>Largest frame payload we will accept, and advertise. RFC 9113 floor is 16384.</summary>
    public int MaxFrameSize { get; init; } = 16384;

    /// <summary>Flow-control window advertised per stream.</summary>
    public int InitialWindowSize { get; init; } = 1 << 20;

    /// <summary>Streams the peer may have open at once.</summary>
    public int MaxConcurrentStreams { get; init; } = 1000;
}
