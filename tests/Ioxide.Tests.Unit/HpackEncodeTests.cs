using ioxide.http2;

namespace Ioxide.Tests;

/// <summary>
/// HPACK encoding of response fields, with the case rule as its subject.
///
/// RFC 9113 8.2.1 makes an uppercase letter in a field name malformed, and a peer may treat the
/// whole message as a stream error. A caller holding headers in the canonical capitalisation its
/// own API uses - "Vary", "Content-Type" - must therefore still put lowercase on the wire. The
/// failure this guards against is quiet in exactly the wrong way: a strict client rejects the
/// HEADERS frame and drops the body, so the response arrives as a clean 200 of length zero.
///
/// QPACK has always applied the same rule for HTTP/3 (see QpackStaticEncodeTests); these are its
/// HTTP/2 counterparts, and the two should not disagree.
/// </summary>
internal static class HpackEncodeTests
{
    public static void Register(Runner runner)
    {
        runner.Test("hpack: a capitalised name resolves against the static table", () =>
        {
            // "content-type" is a static-table name, so a capitalised spelling must reach the same
            // index rather than falling through to a literal - one byte, not fourteen.
            byte[] canonical = Encode("Content-Type"u8, "text/css"u8);
            byte[] lowercase = Encode("content-type"u8, "text/css"u8);

            Assert.True(canonical.AsSpan().SequenceEqual(lowercase),
                "a capitalised static-table name encodes exactly as its lowercase spelling");
        });

        runner.Test("hpack: a capitalised literal name is written out lowercase", () =>
        {
            // "x-custom-header" is not in the static table, so the name goes on the wire as a
            // literal - and that literal is where the case rule has to be applied.
            byte[] encoded = Encode("X-Custom-Header"u8, "1"u8);

            Assert.True(Contains(encoded, "x-custom-header"u8), "the literal name is lowercase");
            Assert.True(!Contains(encoded, "X-Custom-Header"u8), "no capitalised name reaches the wire");
        });

        runner.Test("hpack: a header value keeps its case", () =>
        {
            // Only NAMES are case-insensitive. Lowercasing a value would corrupt an ETag, a
            // Location, a boundary - so the same pass must leave values alone. The name here is
            // deliberately outside the static table, so both halves travel as literals and the
            // difference in treatment is visible on the wire.
            byte[] encoded = Encode("X-Entity-Tag"u8, "\"AbC123\""u8);

            Assert.True(Contains(encoded, "\"AbC123\""u8), "the value keeps its case");
            Assert.True(Contains(encoded, "x-entity-tag"u8), "the name is lowercase");
        });

        runner.Test("hpack: a response's fields all reach the wire lowercase", () =>
        {
            // The header set a static file handler actually produces. "Vary" is the one that
            // caught this: present on a compressed asset, absent on a plain one, so only some
            // responses were rejected and the pattern looked like a body-size problem.
            (byte[] Name, byte[] Value)[] fields =
            [
                ("Content-Type"u8.ToArray(), "text/css"u8.ToArray()),
                ("Content-Length"u8.ToArray(), "8192"u8.ToArray()),
                ("Vary"u8.ToArray(), "Accept-Encoding"u8.ToArray()),
                ("Cache-Control"u8.ToArray(), "public"u8.ToArray()),
                ("Last-Modified"u8.ToArray(), "Wed, 21 Oct 2015 07:28:00 GMT"u8.ToArray()),
            ];

            foreach ((byte[] name, byte[] value) in fields)
            {
                byte[] encoded = Encode(name, value);

                Assert.True(!Contains(encoded, name) || IsLowercase(name),
                    $"'{System.Text.Encoding.ASCII.GetString(name)}' must not reach the wire capitalised");
            }
        });
    }

    private static byte[] Encode(ReadOnlySpan<byte> name, ReadOnlySpan<byte> value)
    {
        byte[] buffer = new byte[HpackEncoder.MaxEncodedLength(name.Length, value.Length)];
        int written = HpackEncoder.Encode(buffer, name, value);
        return buffer[..written];
    }

    private static bool IsLowercase(ReadOnlySpan<byte> bytes)
    {
        foreach (byte b in bytes)
        {
            if (b is >= (byte)'A' and <= (byte)'Z')
            {
                return false;
            }
        }
        return true;
    }

    private static bool Contains(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
        => haystack.IndexOf(needle) >= 0;
}
