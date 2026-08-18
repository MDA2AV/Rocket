using ioxide;
using ioxide.ngtcp2;

namespace Ioxide.Tests;

/// <summary>
/// The deferred-fault machinery: a callback that throws, a recv queue that overflows, and whether
/// either is acted on rather than merely recorded.
/// </summary>
/// <remarks>
/// Reserved for a review pass whose deliverable is a FAILING test. See tests/README.md: a defect
/// that has been reproduced is committed as <c>runner.Pending</c> - it reports PEND while it still
/// fails, and fails the run the moment it starts passing.
///
/// Empty is a legitimate outcome. It means the area was examined and nothing was found that could
/// be made to fail, which is worth more than a test that passes for reasons nobody established.
/// </remarks>
internal static class QuicDeferredFaultTests
{
    public static void Register(Runner runner)
    {
    }
}
