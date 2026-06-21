using ioxide;

namespace ioxide.Kestrel;

/// <summary>Options for the ioxide Kestrel transport.</summary>
public sealed class IoxideTransportOptions
{
    /// <summary>
    /// Number of ioxide reactors (one io_uring ring + one dedicated thread each), load-balanced by
    /// SO_REUSEPORT. Defaults to the processor count.
    /// </summary>
    public int ReactorCount { get; set; } = Environment.ProcessorCount;

    /// <summary>
    /// Optional hook to customize the ioxide <see cref="ServerConfig"/> (ring depth, recv buffer ring,
    /// write slab, zero-copy send, incremental mode, ...). The listen port and reactor count are always
    /// overridden afterwards by the bound endpoint and <see cref="ReactorCount"/>.
    /// </summary>
    public Func<ServerConfig, ServerConfig>? ConfigureServer { get; set; }
}
