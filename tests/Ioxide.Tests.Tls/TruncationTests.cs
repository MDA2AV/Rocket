using ioxide;
using ioxide.tls;

namespace Ioxide.Tests;

/// <summary>
/// Telling a stream that ENDED from a stream that was CUT: close_notify, a bare FIN, and what each
/// looks like to a caller holding only the pipe.
/// </summary>
/// <remarks>
/// Reserved for a review pass whose deliverable is a FAILING test. See tests/README.md: a defect
/// that has been reproduced is committed as <c>runner.Pending</c> - it reports PEND while it still
/// fails, and fails the run the moment it starts passing.
///
/// Empty is a legitimate outcome. It means the area was examined and nothing was found that could
/// be made to fail, which is worth more than a test that passes for reasons nobody established.
/// </remarks>
internal static class TruncationTests
{
    public static void Register(Runner runner)
    {
    }
}
