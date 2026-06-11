using ioxide;

namespace Examples.Pg;

/// <summary>
/// The landing page's "incremental" tab, runnable. The handler is byte-for-byte the shared-ring
/// handler - that's the point: flip ServerConfig.Incremental and size the per-connection knobs;
/// ReturnBuffers routes the refcounted returns internally (kernel 6.12+).
/// </summary>
public static class IncrementalExample
{
    public static Task Handle(Reactor r, Connection conn) => SharedExample.Handle(r, conn);
}
