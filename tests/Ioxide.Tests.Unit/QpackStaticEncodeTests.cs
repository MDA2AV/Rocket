using System.Text;

using ioxide.http3;

namespace Ioxide.Tests;

/// <summary>
/// Response headers encoded against the QPACK static table. The encoder previously used the table
/// for :status alone and spelled every other field name out, so a response repeated names the peer
/// already had by index.
///
/// Checked by decoding what comes out rather than by asserting on bytes: an index that is off by one
/// still produces a well-formed field section, and only a round trip catches that it names the wrong
/// header.
/// </summary>
internal static class QpackStaticEncodeTests
{
    public static void Register(Runner runner)
    {
        runner.Test("qpack: a name and value both in the table cost one byte", () =>
        {
            (string Name, string Value, int Index)[] cases =
            [
                ("content-type", "text/plain", 53),
                ("content-type", "text/html; charset=utf-8", 52),
                ("content-type", "application/json", 46),
                ("accept-ranges", "bytes", 32),
                ("cache-control", "no-cache", 39),
                ("content-encoding", "gzip", 43),
                ("vary", "origin", 60),
            ];

            foreach ((string name, string value, int index) in cases)
            {
                byte[] encoded = Encode(out int written, (name, value));

                // Indexed Field Line: 1 T=1 index(6+), the whole header in one byte.
                Assert.Equal((byte)(0xc0 | index), encoded[written - 1]);
            }
        });

        runner.Test("qpack: a known name with an unknown value references the name only", () =>
        {
            (string Name, string Value)[] cases =
            [
                ("date", "Thu, 14 Aug 2026 13:00:00 GMT"),
                ("location", "/elsewhere"),
                ("etag", "\"abc123\""),
                ("last-modified", "Thu, 14 Aug 2026 00:00:00 GMT"),
                ("server", "ioxide"),
                ("set-cookie", "a=b"),
                ("content-type", "application/x-custom"),
            ];

            foreach ((string name, string value) in cases)
            {
                byte[] encoded = Encode(out int written, (name, value));
                string wire = Encoding.ASCII.GetString(encoded, 0, written);

                // The name must not reach the wire at all - that is the entire point.
                Assert.True(!wire.Contains(name), $"name {name} should not appear on the wire");
                Assert.True(wire.Contains(value), $"value {value} should appear on the wire");

                AssertRoundTrips(encoded, written, name, value);
            }
        });

        runner.Test("qpack: a capitalised name still resolves against the table", () =>
        {
            (string Name, string Value)[] cases =
            [
                ("Content-Type", "text/plain"),
                ("DATE", "Thu, 14 Aug 2026 13:00:00 GMT"),
                ("Cache-Control", "no-cache"),
            ];

            foreach ((string name, string value) in cases)
            {
                byte[] encoded = Encode(out int written, (name, value));
                string wire = Encoding.ASCII.GetString(encoded, 0, written);

                Assert.True(!wire.Contains(name.ToLowerInvariant()), $"{name} should resolve, not be written");

                AssertRoundTrips(encoded, written, name.ToLowerInvariant(), value);
            }
        });

        runner.Test("qpack: a name outside the table is written out, lowercased", () =>
        {
            // HTTP/3 treats a capitalised field name as malformed, so the literal fallback has to
            // lowercase whatever it writes.
            (string Name, string Value)[] cases =
            [
                ("x-custom-header", "value"),
                ("X-Custom-Header", "value"),
                ("X-Request-ID", "r1"),
            ];

            foreach ((string name, string value) in cases)
            {
                byte[] encoded = Encode(out int written, (name, value));
                string wire = Encoding.ASCII.GetString(encoded, 0, written);

                Assert.True(wire.Contains(name.ToLowerInvariant()), $"{name} should be written lowercased");

                AssertRoundTrips(encoded, written, name.ToLowerInvariant(), value);
            }
        });

        runner.Test("qpack: a value is matched case-sensitively", () =>
        {
            // Field values are case-sensitive, so TEXT/PLAIN is not entry 53.
            byte[] encoded = Encode(out int written, ("content-type", "TEXT/PLAIN"));

            Assert.True(Encoding.ASCII.GetString(encoded, 0, written).Contains("TEXT/PLAIN"),
                "a value differing only in case must not resolve to a static entry");

            AssertRoundTrips(encoded, written, "content-type", "TEXT/PLAIN");
        });

        runner.Test("qpack: a response ioxide actually sends shrinks", () =>
        {
            // What Http3Response.Text produces today. The value carries a space after the semicolon,
            // so it misses entry 54 ("text/plain;charset=utf-8") and takes the name reference.
            byte[] encoded = Encode(out int written, ("content-type", "text/plain; charset=utf-8"));

            // Prefix 2 + indexed :status 1 + name ref 2 + value length 1 + 25 value bytes. The name
            // reference costs two bytes rather than one because content-type is index 44, past what
            // the 4-bit prefix holds.
            Assert.Equal(31, written);

            // Spelling the name out instead costs 2 + 12 for it, so 43 in total.
            Assert.True(written < 43, "the name should no longer be on the wire");
        });
    }

    private static byte[] Encode(out int written, params (string Name, string Value)[] headers)
    {
        var response = new Http3Response { Status = 200 };

        foreach ((string name, string value) in headers)
        {
            response.Headers.Add((Encoding.ASCII.GetBytes(name), Encoding.ASCII.GetBytes(value)));
        }

        return Qpack.EncodeResponseFields(response, out written);
    }

    private static void AssertRoundTrips(byte[] encoded, int written, string name, string value)
    {
        var request = new Http3Request();

        Assert.True(Qpack.TryDecodeFieldSection(encoded.AsSpan(0, written), request), "the section should decode");

        // Decoded fields are ranges into an arena until this materialises them.
        request.Freeze();

        foreach ((ReadOnlyMemory<byte> Name, ReadOnlyMemory<byte> Value) field in request.Headers)
        {
            if (Encoding.ASCII.GetString(field.Name.Span) == name)
            {
                Assert.Equal(value, Encoding.ASCII.GetString(field.Value.Span));
                return;
            }
        }

        Assert.True(false, $"decoded section did not contain {name}");
    }
}
