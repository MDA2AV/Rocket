using ioxide;

namespace Ioxide.Tests;

/// <summary>
/// Reactor.TryExtractDcid: the QUIC demux's packet parse, and the first code any datagram from the
/// network reaches. It runs before decryption, before any connection is known, on bytes an attacker
/// fully controls, so it has to reject everything malformed without reading past the buffer.
///
/// RFC 8999 (version-independent invariants) is what it implements: a long header carries an
/// explicit DCID length at offset 5, a short header carries the DCID bare at offset 1 with a length
/// only the endpoint that minted it knows.
/// </summary>
internal static class DemuxParseTests
{
    private const int ShortCidLen = 8;

    public static void Register(Runner runner)
    {
        runner.Test("demux: long header yields its declared DCID", () =>
        {
            // 0x80 = long header, byte 5 = DCID length, then the CID itself.
            byte[] packet = [0xC0, 0, 0, 0, 1, 4, 0xAA, 0xBB, 0xCC, 0xDD, 0x99];

            Assert.True(Reactor.TryExtractDcid(packet, ShortCidLen, out QuicCid cid, out bool longHeader),
                "should parse");
            Assert.True(longHeader, "should report a long header");
            Assert.Equal(4, cid.Length);
        });

        runner.Test("demux: short header slices exactly the local CID length", () =>
        {
            // No 0x80: the DCID starts at offset 1 and runs for exactly ShortCidLen bytes.
            byte[] packet = [0x40, 1, 2, 3, 4, 5, 6, 7, 8, 0xFF, 0xFF];

            Assert.True(Reactor.TryExtractDcid(packet, ShortCidLen, out QuicCid cid, out bool longHeader),
                "should parse");
            Assert.True(!longHeader, "should report a short header");
            Assert.Equal(ShortCidLen, cid.Length);
        });

        runner.Test("demux: truncated packets are rejected, never over-read", () =>
        {
            // Each of these promises more bytes than it carries. The parse must decline rather
            // than read past the span.
            Assert.True(!Reactor.TryExtractDcid([], ShortCidLen, out _, out _),
                "empty packet");
            Assert.True(!Reactor.TryExtractDcid([0xC0, 0, 0, 0], ShortCidLen, out _, out _),
                "long header cut before the length byte");
            Assert.True(!Reactor.TryExtractDcid([0xC0, 0, 0, 0, 1, 8, 0xAA], ShortCidLen, out _, out _),
                "long header declaring 8 CID bytes but carrying one");
            Assert.True(!Reactor.TryExtractDcid([0x40, 1, 2], ShortCidLen, out _, out _),
                "short header shorter than the local CID length");
        });

        runner.Test("demux: an over-long declared CID is refused", () =>
        {
            // 21 exceeds QuicCid.MaxLength. Legal on the wire, but never one of ours, so it is
            // not routable and must not be copied into a fixed-size CID.
            byte[] packet = new byte[64];
            packet[0] = 0xC0;
            packet[5] = 21;

            Assert.True(!Reactor.TryExtractDcid(packet, ShortCidLen, out _, out _),
                "CID longer than QuicCid.MaxLength");
        });

        runner.Test("demux: a zero-length long-header CID parses as empty", () =>
        {
            // Legal: a client may address a server by an empty CID.
            byte[] packet = [0xC0, 0, 0, 0, 1, 0, 0x11, 0x22];

            Assert.True(Reactor.TryExtractDcid(packet, ShortCidLen, out QuicCid cid, out bool longHeader),
                "should parse");
            Assert.True(longHeader, "long header");
            Assert.Equal(0, cid.Length);
        });

        runner.Test("demux: equal CIDs compare and hash alike", () =>
        {
            // The demux is a dictionary keyed by this type, so routing depends on it entirely.
            byte[] first  = [0xC0, 0, 0, 0, 1, 4, 1, 2, 3, 4];
            byte[] second = [0xC0, 9, 9, 9, 9, 4, 1, 2, 3, 4];   // same CID, different header bytes
            byte[] other  = [0xC0, 0, 0, 0, 1, 4, 1, 2, 3, 5];

            Reactor.TryExtractDcid(first,  ShortCidLen, out QuicCid a, out _);
            Reactor.TryExtractDcid(second, ShortCidLen, out QuicCid b, out _);
            Reactor.TryExtractDcid(other,  ShortCidLen, out QuicCid c, out _);

            Assert.True(a.Equals(b), "same CID bytes must be equal");
            Assert.Equal(a.GetHashCode(), b.GetHashCode());
            Assert.True(!a.Equals(c), "a one-byte difference must not be equal");
        });
    }
}
