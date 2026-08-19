using ioxide;
using ioxide.tls;

namespace Ioxide.Tests;

/// <summary>
/// How a peer's name is derived from its certificate: several CNs, a very long DN, and the
/// difference between the rendered subject and the structural CN.
/// </summary>
/// <remarks>
/// Reserved for a review pass whose deliverable is a FAILING test. See tests/README.md: a defect
/// that has been reproduced is committed as <c>runner.Pending</c> - it reports PEND while it still
/// fails, and fails the run the moment it starts passing.
///
/// Empty is a legitimate outcome. It means the area was examined and nothing was found that could
/// be made to fail, which is worth more than a test that passes for reasons nobody established.
/// </remarks>
internal static class IdentitySubjectTests
{
    public static void Register(Runner runner)
    {
    }
}
