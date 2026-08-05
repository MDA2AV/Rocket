using ioxide.httpclient;

namespace Ioxide.Tests;

/// <summary>
/// The ceiling on a response arena. h2 and h3 stream a response in through native callbacks with
/// no length known up front, so the arena is the only thing standing between a hostile or broken
/// origin and an unbounded allocation.
///
/// The cap cannot report by throwing: Append runs inside [UnmanagedCallersOnly] callbacks, and an
/// exception there crosses native frames and takes the process down. It records instead, and the
/// owning connection fails the request once the native call has unwound.
/// </summary>
internal static class ResponseCapTests
{
    public static void Register(Runner runner)
    {
        runner.Test("cap: a response inside the ceiling is untouched", () =>
        {
            using var response = new HttpClientResponse();
            response.SetMaxArenaBytes(1024);

            response.Append(new byte[512]);
            response.Append(new byte[512]);

            Assert.True(!response.Overflowed, "exactly at the ceiling is not an overflow");
            Assert.Equal(1024, response.ArenaLength);
        });

        runner.Test("cap: the append that would cross the ceiling is refused, not served", () =>
        {
            using var response = new HttpClientResponse();
            response.SetMaxArenaBytes(1024);

            response.Append(new byte[1000]);
            Assert.True(!response.Overflowed, "under the ceiling");

            (int Offset, int Length) refused = response.Append(new byte[25]);

            Assert.True(response.Overflowed, "crossing the ceiling must record an overflow");
            Assert.Equal(0, refused.Length);
            Assert.Equal(1000, response.ArenaLength);   // the bytes were dropped, not appended
        });

        runner.Test("cap: once overflowed, later appends stay refused", () =>
        {
            // The callbacks keep firing for the rest of the stream - nothing tells nghttp2/nghttp3
            // to stop - so every subsequent append has to be a no-op rather than resuming growth.
            using var response = new HttpClientResponse();
            response.SetMaxArenaBytes(64);

            response.Append(new byte[65]);
            Assert.True(response.Overflowed, "first oversize append overflows");

            response.Append(new byte[1]);
            response.Append(new byte[1]);

            Assert.Equal(0, response.ArenaLength);
            Assert.True(response.Overflowed, "still overflowed");
        });

        runner.Test("cap: growth past the ceiling never rents a negative size", () =>
        {
            // The trap this replaced: the arena doubled in int, so at 1 GiB the size went negative,
            // Math.Max picked 4096, and the loop doubled its way to int.MinValue for
            // ArrayPool.Rent to throw on - inside a native callback, which is fatal. The ceiling
            // now makes that unreachable and the arithmetic is done in long regardless.
            using var response = new HttpClientResponse();
            response.SetMaxArenaBytes(1 << 20);

            // Many growth rounds from the 4096 floor up to the ceiling.
            for (int i = 0; i < 300; i++)
            {
                response.Append(new byte[4096]);
            }

            Assert.True(response.Overflowed, "300 x 4 KiB exceeds a 1 MiB ceiling");
            Assert.True(response.ArenaLength <= 1 << 20, "arena never exceeds the ceiling");
        });

        runner.Test("cap: Freeze after an overflow is empty rather than out of bounds", () =>
        {
            // Freeze runs inside the same native callbacks Append does. A body range recorded from
            // what ARRIVED rather than what was stored slices past the arena, and that throw
            // crossed nghttp3's frames and took the process down - which is how this was found.
            using var response = new HttpClientResponse();
            response.SetMaxArenaBytes(64);

            (int Offset, int Length) name = response.Append("content-type"u8);
            response.AddHeaderRange(name, name);

            response.Append(new byte[128]);    // refused
            response.SetBodyRange((0, 128));   // what counting arrivals instead of stores records

            response.Freeze();                 // must not throw

            Assert.Equal(0, response.Body.Length);
            Assert.Equal(0, response.Headers.Count);
        });

        runner.Test("cap: an interim reset clears the overflow", () =>
        {
            // A 1xx whose headers overran the cap must not condemn the real response that follows.
            using var response = new HttpClientResponse();
            response.SetMaxArenaBytes(32);

            response.Append(new byte[33]);
            Assert.True(response.Overflowed, "interim overran");

            response.ResetForInterim();

            Assert.True(!response.Overflowed, "reset clears it");
            response.Append(new byte[16]);
            Assert.Equal(16, response.ArenaLength);
        });
    }
}
