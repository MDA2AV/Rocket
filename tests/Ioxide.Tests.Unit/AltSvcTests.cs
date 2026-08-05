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

        runner.Test("alt-svc: 'clear' is a retraction, not merely an absent advertisement", () =>
        {
            // The distinction that matters: "no h3 here" leaves an already-learned port in place,
            // while "clear" has to take it away. Collapsing both to false meant a retired endpoint
            // was retried for the life of the client.
            RingHttpClient.AltSvc cleared = RingHttpClient.ParseAltSvc("clear"u8);
            Assert.True(cleared.Clear, "'clear' must report as a retraction");
            Assert.True(!cleared.Advertises, "and it advertises nothing");

            RingHttpClient.AltSvc unrelated = RingHttpClient.ParseAltSvc("h2=\":443\""u8);
            Assert.True(!unrelated.Clear, "an h2-only header retracts nothing");
            Assert.True(!unrelated.Advertises, "but offers no h3 either");
        });

        runner.Test("alt-svc: ma= sets the lifetime, and its absence takes the RFC default", () =>
        {
            Assert.Equal(3600, RingHttpClient.ParseAltSvc("h3=\":8443\"; ma=3600"u8).MaxAgeSeconds);
            Assert.Equal(0, RingHttpClient.ParseAltSvc("h3=\":8443\"; ma=0"u8).MaxAgeSeconds);

            // RFC 7838 section 3.1: ma is optional and defaults to 24 hours.
            Assert.Equal(86_400, RingHttpClient.ParseAltSvc("h3=\":8443\""u8).MaxAgeSeconds);

            // Other parameters must not be mistaken for it, in either order.
            Assert.Equal(600, RingHttpClient.ParseAltSvc("h3=\":8443\"; persist=1; ma=600"u8).MaxAgeSeconds);
            Assert.Equal(86_400, RingHttpClient.ParseAltSvc("h3=\":8443\"; persist=1"u8).MaxAgeSeconds);

            // Unreadable lifetimes keep the default rather than expiring the advertisement early.
            Assert.Equal(86_400, RingHttpClient.ParseAltSvc("h3=\":8443\"; ma=abc"u8).MaxAgeSeconds);
            Assert.Equal(86_400, RingHttpClient.ParseAltSvc("h3=\":8443\"; ma=-5"u8).MaxAgeSeconds);
        });

        runner.Test("alt-svc: an enormous ma= saturates instead of wrapping into the past", () =>
        {
            // The caller turns this into TickCount64 + seconds * 1000. Unclamped, a big enough
            // value overflowed to a NEGATIVE deadline, which reads as already-expired - so an
            // origin asking to cache its endpoint for a very long time got h3 disabled for the
            // life of the client, silently and permanently. The opposite of what it asked for.
            foreach (string huge in (string[])["10000000000000000", "9223372036854775", "9223372036854775807"])
            {
                RingHttpClient.AltSvc parsed =
                    RingHttpClient.ParseAltSvc(System.Text.Encoding.ASCII.GetBytes($"h3=\":8443\"; ma={huge}"));

                Assert.True(parsed.Advertises, $"ma={huge} should still advertise");

                long deadline = Environment.TickCount64 + (parsed.MaxAgeSeconds * 1000);
                Assert.True(deadline > Environment.TickCount64,
                    $"ma={huge} produced a deadline in the past ({deadline})");
            }
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
