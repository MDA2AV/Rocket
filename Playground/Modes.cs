using ioxide;
using ioxide.file;
using ioxide.http11;
using ioxide.pg;
using Playground.Handlers;
using Playground.Http;
using Playground.Setup;

namespace Playground;

/// <summary>
/// One mode, fully described. Everything the host needs to know - the handlers, the per-reactor
/// services, whether a QUIC listener is required, whether shutdown has to drain nghttp3 - is
/// declared on the row itself, so adding a mode means adding one entry rather than touching
/// several switch statements that have to agree with each other.
/// </summary>
internal sealed class PlaygroundMode
{
    public required string Name { get; init; }
    public required string Summary { get; init; }

    /// <summary>The TCP handler. Every mode serves TCP, even the QUIC ones (:8080 still listens).</summary>
    public required Func<Reactor, TcpConnection, Task> Tcp { get; init; }

    /// <summary>The QUIC handler, when the mode serves HTTP/3.</summary>
    public Func<Reactor, QuicConnection, Task>? Quic { get; init; }

    /// <summary>Per-reactor service registration; runs on the reactor thread.</summary>
    public Action<Reactor>? Start { get; init; }

    /// <summary>
    /// True when the mode holds nghttp3 sessions, so SIGTERM must GOAWAY them before exit. Derived
    /// per-mode instead of matched against a mode-name list - the pure-C# http3 mode serves QUIC but
    /// holds no nghttp3 session, and the quic alias holds one despite not being named "h3".
    /// </summary>
    public bool DrainsNghttp3 { get; init; }

    /// <summary>A QUIC listener is needed exactly when the mode has a QUIC handler.</summary>
    public bool NeedsQuic => Quic is not null;

    /// <summary>Set for <c>file</c> mode, so the host can wire SIGHUP reload to the same snapshot.</summary>
    public StaticAssets? Assets { get; init; }
}

internal static class Modes
{
    /// <summary>Every mode name, for the "unknown mode" message.</summary>
    public static readonly string[] Names =
    [
        "raw", "pipe", "hop", "taskrun", "pg", "file", "proxy", "quic", "h3", "h3-buffered", "http3",
    ];

    /// <summary>
    /// Build the mode named by <paramref name="config"/>. Resources that must be shared across all
    /// reactors (the asset snapshot) or built once from the environment (the fixed response body)
    /// are created here and captured, so the per-connection path does no setup work.
    /// </summary>
    public static PlaygroundMode Resolve(PlaygroundConfig config)
    {
        // One pre-encoded response, shared by every raw-family connection.
        byte[] fixedOk = Responses.BuildFixedOk(config.RawBodyBytes);
        long qpack = config.QpackCapacity;

        switch (config.ModeName)
        {
            case "pipe":
                return new PlaygroundMode
                {
                    Name = "pipe",
                    Summary = "raw workload through the PipeReader/PipeWriter adapters",
                    Tcp = (r, c) => RawHandlers.Pipe(r, c, fixedOk),
                };

            case "hop":
                return new PlaygroundMode
                {
                    Name = "hop",
                    Summary = "raw, but each request bounces through the thread pool",
                    Tcp = (r, c) => RawHandlers.Hop(r, c, fixedOk),
                };

            case "taskrun":
                return new PlaygroundMode
                {
                    Name = "taskrun",
                    Summary = "raw, but each request awaits a Task.Run serialization",
                    Tcp = RawHandlers.TaskRun,
                };

            case "pg":
                return new PlaygroundMode
                {
                    Name = "pg",
                    Summary = $"a PgPool per reactor against {config.Pg.Host}:{config.Pg.Port}",
                    Tcp = PgHandler.Handle,
                    Start = r => PgPool.Start(r, config.Pg),
                };

            case "file":
            {
                SampleAssets.Ensure(config.AssetDir);
                var assets = new StaticAssets(config.AssetDir, config.AssetCacheMaxBytes);

                return new PlaygroundMode
                {
                    Name = "file",
                    Summary = $"{assets.Count} files under {assets.RootDir} (pin <= {config.AssetCacheMaxBytes}B)",
                    Tcp = FileHandler.Handle,
                    Start = r =>
                    {
                        r.AddService(assets);
                        AssetReader.CreatePool(r, readers: 4, bufferBytes: 1 << 20);
                    },
                    Assets = assets,
                };
            }

            case "proxy":
                return new PlaygroundMode
                {
                    Name = "proxy",
                    Summary = $"forwards to {config.Upstream.Host}:{config.Upstream.Port} via ioxide.http11",
                    Tcp = ProxyHandler.Handle,
                    Start = r => HttpClientPool.Start(r, config.Upstream),
                };

            // quic is an alias for h3: both serve the streamed nghttp3 handler, so both need the
            // GOAWAY drain on shutdown.
            case "quic":
            case "h3":
                return new PlaygroundMode
                {
                    Name = config.ModeName,
                    Summary = "HTTP/3 via nghttp3, streamed dispatch",
                    Tcp = (r, c) => RawHandlers.Raw(r, c, fixedOk),
                    Quic = (r, c) => H3Handlers.Streamed(r, c, qpack),
                    DrainsNghttp3 = true,
                };

            case "h3-buffered":
                return new PlaygroundMode
                {
                    Name = "h3-buffered",
                    Summary = "HTTP/3 via nghttp3, buffered dispatch (whole body in req.Body)",
                    Tcp = (r, c) => RawHandlers.Raw(r, c, fixedOk),
                    Quic = (r, c) => H3Handlers.Buffered(r, c, qpack),
                    DrainsNghttp3 = true,
                };

            case "http3":
                return new PlaygroundMode
                {
                    Name = "http3",
                    Summary = "HTTP/3 via the pure-C# ioxide.http3 stack",
                    Tcp = (r, c) => RawHandlers.Raw(r, c, fixedOk),
                    Quic = H3Handlers.PureCSharp,
                    // No nghttp3 session to GOAWAY.
                };

            default:
                if (config.ModeName != "raw")
                {
                    Console.Error.WriteLine(
                        $"[playground] unknown PLAYGROUND_MODE '{config.ModeName}', falling back to raw. "
                      + $"Known modes: {string.Join(", ", Names)}");
                }

                return new PlaygroundMode
                {
                    Name = "raw",
                    Summary = $"fixed {config.RawBodyBytes}-byte plaintext response",
                    Tcp = (r, c) => RawHandlers.Raw(r, c, fixedOk),
                };
        }
    }
}
