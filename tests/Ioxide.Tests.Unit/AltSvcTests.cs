using ioxide.httpclient;

namespace Ioxide.Tests;

/// <summary>
/// RingHttpClient.TryParseH3Port: the whole HTTP/3 negotiation signal. It reads a response header
/// an origin controls and decides where the client sends its next request, so a parse that accepts
/// too much redirects traffic somewhere it was never told to go.
/// </summary>
internal static class AltSvcTests
{
    public static void Register(Runner runner)
    {
        runner.Test("alt-svc: accepts an h3 endpoint on this origin", () =>
        {
            Assert.True(RingHttpClient.TryParseH3Port("h3=\":8443\""u8, out ushort plain) && plain == 8443,
                "plain h3 entry");
            Assert.True(RingHttpClient.TryParseH3Port("h3=\":8443\"; ma=86400"u8, out ushort withMa) && withMa == 8443,
                "ma parameter is ignored, not fatal");
            Assert.True(RingHttpClient.TryParseH3Port("h3-29=\":1111\", h3=\":8443\"; ma=3600"u8, out ushort later)
                        && later == 8443,
                "a draft token first must not hide the real entry behind it");
        });

        runner.Test("alt-svc: refuses an endpoint on a different host", () =>
        {
            // This is the security-relevant case: honouring it would send this origin's requests,
            // and anything they carry, to a host the caller never configured.
            Assert.True(!RingHttpClient.TryParseH3Port("h3=\"other.example:8443\""u8, out _),
                "named host rejected");
            Assert.True(!RingHttpClient.TryParseH3Port("h3=\"[::1]:8443\""u8, out _),
                "literal address rejected");
        });

        runner.Test("alt-svc: refuses protocols we do not speak and impossible ports", () =>
        {
            Assert.True(!RingHttpClient.TryParseH3Port("clear"u8, out _),
                "'clear' retracts every advertisement");
            Assert.True(!RingHttpClient.TryParseH3Port("h2=\":443\""u8, out _),
                "h2 is not h3");
            Assert.True(!RingHttpClient.TryParseH3Port("h3-29=\":8443\""u8, out _),
                "a draft we do not implement");
            Assert.True(!RingHttpClient.TryParseH3Port("h3=\":0\""u8, out _),
                "port 0 is not connectable");
            Assert.True(!RingHttpClient.TryParseH3Port("h3=\":99999\""u8, out _),
                "port above 65535");
        });

        runner.Test("alt-svc: malformed input terminates instead of hanging", () =>
        {
            // The value arrives from the network, so every shape has to reach a decision. A parse
            // that loops here would wedge the reactor thread outright.
            foreach (string junk in new[]
                     {
                         "", "garbage", ",", ",,,,", "h3", "h3=", "h3=\"\"", "h3=:", "=\":8443\"",
                         "h3=\":8443", "h3=\"8443\"", ";;;", "h3=\":8443\";", "h3=\":8443\",",
                     })
            {
                byte[] value = System.Text.Encoding.ASCII.GetBytes(junk);
                RingHttpClient.TryParseH3Port(value, out _);   // must simply return
            }
        });
    }
}
