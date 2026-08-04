using System.Runtime.InteropServices;
using ioxide;

namespace Playground.Shared;

/// <summary>
/// What one sample is: the handlers, whatever per-reactor services they need, and the signal hooks
/// that apply to it. Everything is declared here rather than discovered, so <see cref="PlaygroundHost"/>
/// never has to know which sample it is running.
/// </summary>
public sealed class PlaygroundSample
{
    /// <summary>Short name, for the startup banner.</summary>
    public required string Name { get; init; }

    /// <summary>One line describing what this sample demonstrates.</summary>
    public required string Summary { get; init; }

    /// <summary>The TCP handler. Every sample serves TCP, even the HTTP/3 ones (:8080 still listens).</summary>
    public required Func<Reactor, TcpConnection, Task> Tcp { get; init; }

    /// <summary>The QUIC handler, when the sample serves HTTP/3.</summary>
    public Func<Reactor, QuicConnection, Task>? Quic { get; init; }

    /// <summary>
    /// QUIC listener settings. The sample builds these itself because the engine lives in
    /// ioxide.ngtcp2 - keeping it out of here is what lets the TCP samples avoid that reference.
    /// </summary>
    public QuicOptions? QuicOptions { get; init; }

    /// <summary>Per-reactor service registration; runs on the reactor thread.</summary>
    public Action<Reactor>? Start { get; init; }

    /// <summary>SIGHUP hook - e.g. reloading a static asset snapshot. Prints its own message.</summary>
    public Action? OnReload { get; init; }

    /// <summary>
    /// SIGTERM hook - e.g. GOAWAY every live HTTP/3 connection. Runs OFF the reactor threads, gets a
    /// grace period for in-flight requests, then the process exits.
    /// </summary>
    public Action? OnDrain { get; init; }
}

/// <summary>
/// Runs a sample: builds the engine config from the environment, wires the sample to one reactor per
/// thread, and blocks until they exit. Every Playground sample's Main is a call to <see cref="Run"/>.
/// </summary>
public static class PlaygroundHost
{
    /// <summary>How long in-flight requests get to finish after SIGTERM.</summary>
    private const int DrainGraceMs = 2000;

    public static int Run(PlaygroundSample sample)
    {
        ServerConfig config = EngineConfig.FromEnvironment(sample.QuicOptions);

        // Reload on SIGHUP (e.g. `kill -HUP <pid>` after a deploy).
        using IDisposable? reload = sample.OnReload is null
            ? null
            : PosixSignalRegistration.Create(PosixSignal.SIGHUP, context =>
            {
                context.Cancel = true;   // handle it; don't let the default action terminate us
                sample.OnReload();
            });

        // Graceful shutdown. Without it the process dies mid-request and clients see resets.
        using IDisposable? drain = sample.OnDrain is null
            ? null
            : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            {
                context.Cancel = true;
                Console.WriteLine("[playground] SIGTERM: draining connections...");
                sample.OnDrain();
                Thread.Sleep(DrainGraceMs);
                Console.WriteLine("[playground] drain complete, exiting");
                Environment.Exit(0);
            });

        Console.WriteLine($"[playground] {config.ReactorCount} reactors on :{config.Tcp.Port} "
                        + $"({sample.Name}) - {sample.Summary}");

        var threads = new Thread[config.ReactorCount];

        for (int i = 0; i < threads.Length; i++)
        {
            var reactor = new Reactor(i, config)
            {
                TcpHandle = sample.Tcp,
                QuicHandle = sample.Quic,
                // Runs on the reactor thread, so every client opened there rides that reactor's ring.
                OnStart = sample.Start,
            };

            threads[i] = new Thread(reactor.Run)
            {
                Name = $"reactor-{i}",
                IsBackground = false,
            };

            threads[i].Start();
        }

        foreach (Thread thread in threads)
        {
            thread.Join();
        }

        return 0;
    }
}
