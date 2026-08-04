using System.Runtime.InteropServices;
using ioxide;
using ioxide.ngtcp2;
using Playground.Handlers;
using Playground.Setup;

namespace Playground;

/// <summary>
/// A host for the ioxide engine. PLAYGROUND_MODE picks the mode; every mode declares its own
/// handlers and services in <see cref="Modes"/>, so this file only wires them to reactors. See
/// Playground/README.md for the full set of environment knobs.
/// </summary>
internal static class Program
{
    private static int Main()
    {
        PlaygroundConfig config = PlaygroundConfig.FromEnvironment();
        PlaygroundMode mode = Modes.Resolve(config);

        // ngtcp2+picotls next to the TCP listener, ALPN pinned to h3. One engine for the whole
        // server; every reactor binds the UDP port via SO_REUSEPORT and demuxes its own flows.
        using QuicEngine? quicEngine = CreateQuicEngine(config, mode, out QuicOptions? quicOptions);

        ServerConfig serverConfig = config.ToServerConfig(quicOptions);

        using IDisposable? reloadOnSighup = RegisterAssetReload(mode);
        using IDisposable? drainOnSigterm = RegisterH3Drain(mode);

        Console.WriteLine($"[playground] {serverConfig.ReactorCount} reactors on :{config.TcpPort} "
                        + $"(mode={mode.Name}) - {mode.Summary}");

        var threads = new Thread[serverConfig.ReactorCount];

        for (int i = 0; i < threads.Length; i++)
        {
            var reactor = new Reactor(i, serverConfig)
            {
                TcpHandle = mode.Tcp,
                QuicHandle = mode.Quic,
                // Runs on the reactor thread, so every client opened there rides that reactor's ring.
                OnStart = mode.Start,
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

    private static QuicEngine? CreateQuicEngine(PlaygroundConfig config, PlaygroundMode mode, out QuicOptions? options)
    {
        if (!mode.NeedsQuic)
        {
            options = null;
            return null;
        }

        (string certPath, string keyPath) = QuicCert.Ensure(config.QuicCertPath, config.QuicKeyPath);
        var engine = new QuicEngine(certPath, keyPath, cidLength: 8, alpn: ["h3"]);

        options = new QuicOptions
        {
            Port = config.QuicPort,
            LocalCidLength = 8,
            ConnectionFactory = engine.CreateFactory(),
        };

        Console.WriteLine($"[playground] {mode.Name} on udp :{config.QuicPort} (ngtcp2 {QuicEngine.NativeVersion()})");
        return engine;
    }

    /// <summary>
    /// Reload the assets on SIGHUP (e.g. <c>kill -HUP &lt;pid&gt;</c> after a deploy): a fresh
    /// snapshot is opened and swapped in atomically; the old descriptors close after a grace.
    /// </summary>
    private static IDisposable? RegisterAssetReload(PlaygroundMode mode)
    {
        if (mode.Assets is not { } assets) return null;

        return PosixSignalRegistration.Create(PosixSignal.SIGHUP, context =>
        {
            context.Cancel = true;   // handle it; don't let the default action terminate us
            assets.Reload();
            Console.WriteLine($"[playground] reloaded - now serving {assets.Count} files");
        });
    }

    /// <summary>
    /// Graceful shutdown for the nghttp3 modes: SIGTERM GOAWAYs every live connection, gives
    /// in-flight requests a grace period to finish, then exits. Without this the process dies
    /// mid-request and clients see resets.
    /// </summary>
    private static IDisposable? RegisterH3Drain(PlaygroundMode mode)
    {
        if (!mode.DrainsNghttp3) return null;

        return PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            Console.WriteLine("[playground] SIGTERM: draining h3 connections (GOAWAY)...");
            H3Handlers.ShutdownAll();
            Thread.Sleep(2000);   // grace period for in-flight requests
            Console.WriteLine("[playground] drain complete, exiting");
            Environment.Exit(0);
        });
    }
}
