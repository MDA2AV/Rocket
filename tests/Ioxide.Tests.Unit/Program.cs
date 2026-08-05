namespace Ioxide.Tests;

/// <summary>
/// Unit tests for the pure logic the runtime cannot afford to get wrong: the QUIC demux parse, the
/// Alt-Svc negotiation parser, and the shared HTTP message types. No sockets, no sidecars, no
/// timing - so these run anywhere and a failure is always a real defect rather than a flake.
/// </summary>
internal static class Program
{
    private static int Main()
    {
        var runner = new Runner();

        DemuxParseTests.Register(runner);
        AltSvcTests.Register(runner);
        MessageTests.Register(runner);

        return runner.Summary();
    }
}
