using ioxide;
using ioxide.ngtcp2;
using Playground.Shared.Setup;

namespace Playground.Shared.Quic;

/// <summary>
/// The QUIC listener every HTTP/3 sample stands up: ngtcp2 + picotls next to the TCP listener, ALPN
/// pinned to h3. One engine for the whole server; every reactor binds the UDP port via SO_REUSEPORT
/// and demuxes its own flows.
/// </summary>
public static class QuicSetup
{
    /// <summary>
    /// Build the engine and the listener options from the environment. The caller keeps the engine
    /// alive for the process lifetime and disposes it on the way out.
    /// </summary>
    public static (QuicEngine Engine, QuicOptions Options) FromEnvironment(string sampleName)
    {
        (string certPath, string keyPath) = QuicCert.Ensure(
            Env.StrOrNull("PLAYGROUND_QUIC_CERT"),
            Env.StrOrNull("PLAYGROUND_QUIC_KEY"));

        var engine = new QuicEngine(certPath, keyPath, cidLength: 8, alpn: ["h3"]);
        ushort port = Env.Port("PLAYGROUND_QUIC_PORT", 8443);

        var options = new QuicOptions
        {
            Port = port,
            LocalCidLength = 8,
            ConnectionFactory = engine.CreateFactory(),
        };

        Console.WriteLine($"[playground] {sampleName} on udp :{port} (ngtcp2 {QuicEngine.NativeVersion()})");
        return (engine, options);
    }
}
