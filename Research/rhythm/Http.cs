using System.Buffers.Text;

namespace Rhythm;

/// <summary>
/// The workload, decoupled from the io_uring reactor: a hand-rolled HTTP/1.1
/// request parser + response serializer that operates purely on spans.
///
/// The reactor calls <see cref="Process"/> with a slice of the connection's recv
/// buffer and the free tail of its write buffer; it returns how many recv bytes
/// it consumed and (out) how many write bytes it produced. It never touches the
/// ring, sockets, or threads — which is exactly the seam where an async workload
/// (e.g. an io_uring-native DB call) would later turn this into a per-connection
/// state machine the reactor drives via further CQEs.
///
/// Endpoints:
///   GET/POST /baseline11?a=&b=  -> text/plain "a + b (+ body)"
///   GET      /pipeline          -> text/plain "ok"
///   GET      /json/{count}?m=N  -> application/json, serialized per request
/// </summary>
internal static class Http
{
    /// Parse one request from <paramref name="buf"/> and serialize its response
    /// into <paramref name="write"/> (from index 0). Returns bytes consumed from
    /// buf, 0 if the request isn't fully buffered, or -1 on error / no write room.
    public static int Process(ReadOnlySpan<byte> buf, Span<byte> write, Dataset ds, out int wrote, out bool close)
    {
        wrote = 0;
        close = false;

        int he = buf.IndexOf("\r\n\r\n"u8);
        if (he < 0) return 0;
        ReadOnlySpan<byte> head = buf[..he];

        int rlEnd = head.IndexOf("\r\n"u8);
        if (rlEnd < 0) rlEnd = head.Length;
        ReadOnlySpan<byte> reqLine = head[..rlEnd];

        ReadOnlySpan<byte> target = default;
        int sp1 = reqLine.IndexOf((byte)' ');
        if (sp1 >= 0)
        {
            ReadOnlySpan<byte> rest = reqLine[(sp1 + 1)..];
            int sp2 = rest.IndexOf((byte)' ');
            target = sp2 >= 0 ? rest[..sp2] : rest;
        }

        int contentLength = -1;
        bool chunked = false;
        bool reqClose = false;
        ReadOnlySpan<byte> hdrs = head[Math.Min(rlEnd + 2, head.Length)..];
        while (hdrs.Length > 0)
        {
            int nl = hdrs.IndexOf("\r\n"u8);
            ReadOnlySpan<byte> line = nl >= 0 ? hdrs[..nl] : hdrs;
            int colon = line.IndexOf((byte)':');
            if (colon >= 0)
            {
                ReadOnlySpan<byte> name = line[..colon];
                ReadOnlySpan<byte> val = Trim(line[(colon + 1)..]);
                if (CiEq(name, "content-length"u8)) { if (Utf8Parser.TryParse(val, out int cl, out _)) contentLength = cl; }
                else if (CiEq(name, "transfer-encoding"u8) && CiContains(val, "chunked"u8)) chunked = true;
                else if (CiEq(name, "connection"u8) && CiEq(val, "close"u8)) reqClose = true;
            }
            if (nl < 0) break;
            hdrs = hdrs[(nl + 2)..];
        }

        int bodyStart = he + 4;
        long bodyInt;
        int total;
        if (chunked)
        {
            if (!DecodeChunked(buf[bodyStart..], out bodyInt, out int used)) return 0;
            total = bodyStart + used;
        }
        else if (contentLength > 0)
        {
            if (buf.Length < bodyStart + contentLength) return 0;
            bodyInt = ParseLoose(buf.Slice(bodyStart, contentLength));
            total = bodyStart + contentLength;
        }
        else { bodyInt = 0; total = bodyStart; }

        int pos = 0;
        if (!Respond(write, ref pos, target, bodyInt, reqClose, ds)) return -1; // no write room
        wrote = pos;
        close = reqClose;
        return total;
    }

    // ── response serialization ──────────────────────────────────────────────
    private static bool Respond(Span<byte> w, ref int pos, ReadOnlySpan<byte> target, long bodyInt, bool close, Dataset ds)
    {
        int q = target.IndexOf((byte)'?');
        ReadOnlySpan<byte> path = q >= 0 ? target[..q] : target;
        ReadOnlySpan<byte> query = q >= 0 ? target[(q + 1)..] : default;

        if (path.SequenceEqual("/pipeline"u8))
            return WriteText(w, ref pos, "ok"u8, close);

        if (path.StartsWith("/json/"u8))
        {
            ReadOnlySpan<byte> tail = path[6..];
            if (Utf8Parser.TryParse(tail, out int count, out int used) && used == tail.Length
                && count >= 1 && count <= ds.Count)
                return WriteJson(w, ref pos, count, ParseM(query), ds, close);
            return Write404(w, ref pos, close);
        }

        long sum = SumAB(query) + bodyInt;
        Span<byte> num = stackalloc byte[24];
        Utf8Formatter.TryFormat(sum, num, out int n);
        return WriteText(w, ref pos, num[..n], close);
    }

    private static bool WriteText(Span<byte> w, ref int pos, ReadOnlySpan<byte> body, bool close)
    {
        if (w.Length - pos < body.Length + 96) return false;
        Wr(w, ref pos, "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: "u8);
        WrLong(w, ref pos, body.Length);
        Wr(w, ref pos, close ? "\r\nConnection: close\r\n\r\n"u8 : "\r\n\r\n"u8);
        Wr(w, ref pos, body);
        return true;
    }

    private static bool Write404(Span<byte> w, ref int pos, bool close)
    {
        if (w.Length - pos < 128) return false;
        Wr(w, ref pos, "HTTP/1.1 404 Not Found\r\nContent-Type: text/plain\r\nContent-Length: 9\r\n"u8);
        if (close) Wr(w, ref pos, "Connection: close\r\n"u8);
        Wr(w, ref pos, "\r\nNot Found"u8);
        return true;
    }

    private static bool WriteJson(Span<byte> w, ref int pos, int count, long m, Dataset ds, bool close)
    {
        if (w.Length - pos < count * 256 + 160) return false;

        Wr(w, ref pos, "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: "u8);
        int clOff = pos;
        Wr(w, ref pos, "000000\r\n"u8);
        if (close) Wr(w, ref pos, "Connection: close\r\n"u8);
        Wr(w, ref pos, "\r\n"u8);
        int bodyStart = pos;

        Wr(w, ref pos, "{\"items\":["u8);
        Item[] items = ds.Items;
        for (int i = 0; i < count; i++)
        {
            if (i > 0) Wr(w, ref pos, ","u8);
            ref readonly Item it = ref items[i];
            Wr(w, ref pos, "{\"id\":"u8); WrLong(w, ref pos, it.Id);
            Wr(w, ref pos, ",\"name\":\""u8); Wr(w, ref pos, it.Name);
            Wr(w, ref pos, "\",\"category\":\""u8); Wr(w, ref pos, it.Category);
            Wr(w, ref pos, "\",\"price\":"u8); WrLong(w, ref pos, it.Price);
            Wr(w, ref pos, ",\"quantity\":"u8); WrLong(w, ref pos, it.Quantity);
            Wr(w, ref pos, it.Active ? ",\"active\":true,\"tags\":["u8 : ",\"active\":false,\"tags\":["u8);
            byte[][] tags = it.Tags;
            for (int t = 0; t < tags.Length; t++)
            {
                if (t > 0) Wr(w, ref pos, ","u8);
                Wr(w, ref pos, "\""u8); Wr(w, ref pos, tags[t]); Wr(w, ref pos, "\""u8);
            }
            Wr(w, ref pos, "],\"rating\":{\"score\":"u8); WrLong(w, ref pos, it.Score);
            Wr(w, ref pos, ",\"count\":"u8); WrLong(w, ref pos, it.RatingCount);
            Wr(w, ref pos, "},\"total\":"u8); WrLong(w, ref pos, it.Price * it.Quantity * m);
            Wr(w, ref pos, "}"u8);
        }
        Wr(w, ref pos, "],\"count\":"u8); WrLong(w, ref pos, count); Wr(w, ref pos, "}"u8);

        int bodyLen = pos - bodyStart;
        for (int d = clOff + 5; d >= clOff; d--) { w[d] = (byte)('0' + bodyLen % 10); bodyLen /= 10; }
        return true;
    }

    // ── tiny writers / parsers ──────────────────────────────────────────────
    private static void Wr(Span<byte> w, ref int pos, ReadOnlySpan<byte> src) { src.CopyTo(w[pos..]); pos += src.Length; }
    private static void WrLong(Span<byte> w, ref int pos, long v) { Utf8Formatter.TryFormat(v, w[pos..], out int n); pos += n; }

    private static long SumAB(ReadOnlySpan<byte> query)
    {
        long a = 0, b = 0;
        while (query.Length > 0)
        {
            int amp = query.IndexOf((byte)'&');
            ReadOnlySpan<byte> kv = amp >= 0 ? query[..amp] : query;
            int eq = kv.IndexOf((byte)'=');
            if (eq >= 0)
            {
                ReadOnlySpan<byte> k = kv[..eq];
                if (k.SequenceEqual("a"u8)) a = ParseLoose(kv[(eq + 1)..]);
                else if (k.SequenceEqual("b"u8)) b = ParseLoose(kv[(eq + 1)..]);
            }
            if (amp < 0) break;
            query = query[(amp + 1)..];
        }
        return a + b;
    }

    private static long ParseM(ReadOnlySpan<byte> query)
    {
        while (query.Length > 0)
        {
            int amp = query.IndexOf((byte)'&');
            ReadOnlySpan<byte> kv = amp >= 0 ? query[..amp] : query;
            if (kv.Length >= 2 && kv[0] == (byte)'m' && kv[1] == (byte)'=')
            {
                Utf8Parser.TryParse(kv[2..], out long m, out _);
                return m;
            }
            if (amp < 0) break;
            query = query[(amp + 1)..];
        }
        return 1;
    }

    private static bool DecodeChunked(ReadOnlySpan<byte> buf, out long bodyInt, out int used)
    {
        bodyInt = 0; used = 0;
        Span<byte> body = stackalloc byte[256];
        int blen = 0, pos = 0;
        while (true)
        {
            int nl = buf[pos..].IndexOf("\r\n"u8);
            if (nl < 0) return false;
            if (!ParseHex(buf.Slice(pos, nl), out int size)) return false;
            pos += nl + 2;
            if (size == 0)
            {
                int end = buf[pos..].IndexOf("\r\n"u8);
                if (end < 0) return false;
                used = pos + end + 2;
                bodyInt = ParseLoose(body[..blen]);
                return true;
            }
            if (buf.Length < pos + size + 2) return false;
            if (blen + size <= body.Length) { buf.Slice(pos, size).CopyTo(body[blen..]); blen += size; }
            pos += size;
            if (!buf.Slice(pos, 2).SequenceEqual("\r\n"u8)) return false;
            pos += 2;
        }
    }

    private static ReadOnlySpan<byte> Trim(ReadOnlySpan<byte> b)
    {
        int s = 0, e = b.Length;
        while (s < e && (b[s] == (byte)' ' || b[s] == (byte)'\t')) s++;
        while (e > s && (b[e - 1] == (byte)' ' || b[e - 1] == (byte)'\t')) e--;
        return b[s..e];
    }

    private static bool CiEq(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (Low(a[i]) != Low(b[i])) return false;
        return true;
    }

    private static bool CiContains(ReadOnlySpan<byte> h, ReadOnlySpan<byte> n)
    {
        if (n.Length == 0 || h.Length < n.Length) return false;
        for (int i = 0; i + n.Length <= h.Length; i++) if (CiEq(h.Slice(i, n.Length), n)) return true;
        return false;
    }

    private static byte Low(byte c) => (byte)(c >= 'A' && c <= 'Z' ? c + 32 : c);

    private static long ParseLoose(ReadOnlySpan<byte> s)
    {
        int i = 0;
        while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) i++;
        bool neg = false;
        if (i < s.Length && s[i] == '-') { neg = true; i++; }
        long n = 0;
        while (i < s.Length && s[i] >= '0' && s[i] <= '9') { n = n * 10 + (s[i] - '0'); i++; }
        return neg ? -n : n;
    }

    private static bool ParseHex(ReadOnlySpan<byte> b, out int val)
    {
        val = 0; bool any = false;
        foreach (byte c in b)
        {
            int d;
            if (c >= '0' && c <= '9') d = c - '0';
            else if (c >= 'a' && c <= 'f') d = c - 'a' + 10;
            else if (c >= 'A' && c <= 'F') d = c - 'A' + 10;
            else if (c == ';' || c == ' ') break;
            else return any;
            val = val * 16 + d; any = true;
        }
        return any;
    }
}
