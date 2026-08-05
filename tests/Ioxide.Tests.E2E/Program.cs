namespace Ioxide.Tests;

/// <summary>
/// The engine suite: reactor lifecycle, the TCP read and write paths, hardening against malformed
/// or hostile peers, UDP, the QUIC transport and its ngtcp2 engine, and the two HTTP/3 layers.
/// Everything here runs against real servers over real sockets and needs no external dependency.
/// </summary>
internal static class Program
{
    private static int Main()
    {
        var runner = new Runner();

        CoreTests.Register(runner);
        HardeningTests.Register(runner);
        UdpTests.Register(runner);
        QuicTests.Register(runner);
        QuicEngineTests.Register(runner);
        H3Tests.Register(runner);
        Http3Tests.Register(runner);

        return runner.Summary();
    }
}
