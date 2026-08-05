using System.Text;
using ioxide.httpclient;

namespace Ioxide.Tests;

/// <summary>
/// ResponseAssembly decides where a response's body begins inside the arena. h2 and h3 both drive
/// it from their header/data callbacks, so getting it wrong does not throw - it silently hands back
/// the wrong bytes as the body, which is the worst failure mode a client has.
///
/// The sequences below are the ones the wire actually produces: one field section then a body, an
/// interim (1xx) section before the real one, and trailers after the body.
/// </summary>
internal static class ResponseAssemblyTests
{
    public static void Register(Runner runner)
    {
        runner.Test("assembly: body starts after the header bytes", () =>
        {
            using var response = new HttpClientResponse();
            var assembly = new ResponseAssembly();

            AppendHeader(response, "content-type"u8, "text/plain"u8);
            response.Status = 200;

            Assert.True(!assembly.EndFieldSection(response), "200 is final, not interim");

            int headerBytes = response.ArenaLength;
            Assert.Equal(headerBytes, assembly.BodyStart);

            AppendBody(response, ref assembly, "hello"u8);

            Assert.Equal((headerBytes, 5), assembly.BodyRange);
            Assert.Equal("hello", BodyText(response, assembly));
        });

        runner.Test("assembly: a 1xx interim is discarded and the real body still slices right", () =>
        {
            // The regression that motivated this type: h3 treated the final response's field
            // section as trailers because the interim had already set HeadersDone, leaving
            // BodyStart inside the final headers. The body then came back as header bytes.
            using var response = new HttpClientResponse();
            var assembly = new ResponseAssembly();

            // 103 Early Hints, with a header long enough that a stale BodyStart is unmistakable.
            AppendHeader(response, "link"u8, "</style.css>; rel=preload"u8);
            response.Status = 103;

            Assert.True(assembly.EndFieldSection(response), "1xx must report as interim");
            Assert.Equal(0, response.ArenaLength);
            Assert.Equal(0, response.Status);
            Assert.Equal(0, assembly.BodyStart);
            Assert.True(!assembly.HeadersDone, "the interim section must not count as the headers");

            // The real response follows on the same stream.
            AppendHeader(response, "content-type"u8, "text/plain"u8);
            response.Status = 200;

            Assert.True(!assembly.EndFieldSection(response), "200 is final");
            Assert.Equal(response.ArenaLength, assembly.BodyStart);

            AppendBody(response, ref assembly, "real body"u8);
            response.Freeze();

            Assert.Equal("real body", BodyText(response, assembly));
            Assert.Equal(1, response.Headers.Count);
            Assert.Equal("content-type", Text(response.Headers[0].Key));
        });

        runner.Test("assembly: several interims in a row all get dropped", () =>
        {
            // 100 Continue then 103 Early Hints before the real response is legal, and each one
            // has to reset rather than accumulate.
            using var response = new HttpClientResponse();
            var assembly = new ResponseAssembly();

            foreach (int interim in (int[])[100, 103, 103])
            {
                AppendHeader(response, "x-hint"u8, "value"u8);
                response.Status = interim;
                Assert.True(assembly.EndFieldSection(response), $"{interim} must report as interim");
                Assert.Equal(0, response.ArenaLength);
            }

            AppendHeader(response, "server"u8, "ioxide"u8);
            response.Status = 204;
            Assert.True(!assembly.EndFieldSection(response), "204 is final");

            AppendBody(response, ref assembly, "x"u8);
            Assert.Equal("x", BodyText(response, assembly));
        });

        runner.Test("assembly: trailers after the body leave the body range alone", () =>
        {
            // A second field section AFTER a final response is trailers. Moving BodyStart there
            // would slice past the body into the trailer bytes.
            using var response = new HttpClientResponse();
            var assembly = new ResponseAssembly();

            AppendHeader(response, "content-type"u8, "text/plain"u8);
            response.Status = 200;
            assembly.EndFieldSection(response);

            AppendBody(response, ref assembly, "chunked payload"u8);
            (int Offset, int Length) afterBody = assembly.BodyRange;

            // Trailer section arrives on the same stream.
            AppendHeader(response, "x-checksum"u8, "deadbeef"u8);
            Assert.True(!assembly.EndFieldSection(response), "trailers are not interim");

            Assert.Equal(afterBody, assembly.BodyRange);
            Assert.Equal("chunked payload", BodyText(response, assembly));
        });
    }

    private static void AppendHeader(HttpClientResponse response, ReadOnlySpan<byte> name,
        ReadOnlySpan<byte> value)
    {
        (int Offset, int Length) nameRange = response.Append(name);
        (int Offset, int Length) valueRange = response.Append(value);
        response.AddHeaderRange(nameRange, valueRange);
    }

    // What the DATA callbacks do: bytes into the arena, length onto the running body count.
    private static void AppendBody(HttpClientResponse response, ref ResponseAssembly assembly,
        ReadOnlySpan<byte> data)
    {
        response.Append(data);
        assembly.BodyLength += data.Length;
    }

    private static string BodyText(HttpClientResponse response, ResponseAssembly assembly)
    {
        response.SetBodyRange(assembly.BodyRange);
        response.Freeze();
        return Encoding.ASCII.GetString(response.Body.Span);
    }

    private static string Text(ReadOnlyMemory<byte> value) => Encoding.ASCII.GetString(value.Span);
}
