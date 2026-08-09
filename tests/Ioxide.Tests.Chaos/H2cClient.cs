using System.Net.Sockets;
using System.Text;

namespace Ioxide.Tests;

/// <summary>
/// A raw h2c (cleartext, prior-knowledge HTTP/2) client - just enough framing to open a connection,
/// throw hand-built frames at the server, and see whether a request comes back answered. It never
/// decodes the response headers (the server's handler always answers 200); a HEADERS frame arriving
/// on the request stream is the liveness signal. Encoding is the simplest legal HPACK: literal, no
/// indexing, no Huffman.
/// </summary>
public sealed class H2cClient : IDisposable
{
    // RFC 9113 3.4 - the connection preface every h2c client sends first.
    private static readonly byte[] Preface = "PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"u8.ToArray();

    private const byte Data = 0x0, Headers = 0x1, Settings = 0x4, GoAway = 0x7;
    private const byte EndStream = 0x1, EndHeaders = 0x4, Ack = 0x1;

    private readonly TcpClient _tcp;
    private readonly NetworkStream _s;

    public H2cClient(int port, int timeoutMs = 6000)
    {
        _tcp = new TcpClient();
        _tcp.Connect("127.0.0.1", port);
        _tcp.ReceiveTimeout = timeoutMs;
        _s = _tcp.GetStream();
    }

    /// <summary>Preface plus an empty SETTINGS frame - a well-formed connection opening.</summary>
    public void Open()
    {
        _s.Write(Preface);
        WriteFrame(Settings, flags: 0, streamId: 0, ReadOnlySpan<byte>.Empty);
    }

    /// <summary>Send a complete request (HEADERS with END_HEADERS|END_STREAM) on a stream.</summary>
    public void Request(int streamId, string method = "GET", string path = "/")
        => WriteFrame(Headers, EndHeaders | EndStream, streamId, Hpack(method, path));

    /// <summary>Raw preface bytes with no framing - for the bad-preface assault.</summary>
    public void WriteRaw(ReadOnlySpan<byte> bytes)
    {
        _s.Write(bytes);
        _s.Flush();
    }

    public void WriteFrame(byte type, byte flags, int streamId, ReadOnlySpan<byte> payload)
        => WriteFrameHeader(type, flags, streamId, payload.Length, payload);

    /// <summary>
    /// A frame header whose declared length may DISAGREE with the bytes that follow - the primitive
    /// behind the oversize-frame (declare huge, send nothing) and truncated-frame (declare N, send
    /// fewer) assaults.
    /// </summary>
    public void WriteFrameHeader(byte type, byte flags, int streamId, int declaredLen, ReadOnlySpan<byte> actual)
    {
        Span<byte> hdr = stackalloc byte[9];
        hdr[0] = (byte)(declaredLen >> 16);
        hdr[1] = (byte)(declaredLen >> 8);
        hdr[2] = (byte)declaredLen;
        hdr[3] = type;
        hdr[4] = flags;
        hdr[5] = (byte)(streamId >> 24);
        hdr[6] = (byte)(streamId >> 16);
        hdr[7] = (byte)(streamId >> 8);
        hdr[8] = (byte)streamId;
        _s.Write(hdr);
        if (!actual.IsEmpty)
        {
            _s.Write(actual);
        }
        _s.Flush();
    }

    /// <summary>
    /// Pump inbound frames until a HEADERS lands on <paramref name="streamId"/> (the server answered)
    /// or the deadline passes. Server SETTINGS are ACKed as they arrive; a GOAWAY ends the wait.
    /// </summary>
    public bool AwaitResponse(int streamId, int timeoutMs = 4000)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (!TryReadFrame(out byte type, out byte flags, out int sid, out _))
            {
                return false;
            }
            if (type == Settings && (flags & Ack) == 0)
            {
                WriteFrame(Settings, Ack, 0, ReadOnlySpan<byte>.Empty);   // ACK the server's SETTINGS
            }
            else if (type == Headers && sid == streamId)
            {
                return true;
            }
            else if (type == GoAway)
            {
                return false;
            }
        }
        return false;
    }

    private bool TryReadFrame(out byte type, out byte flags, out int streamId, out byte[] payload)
    {
        type = 0;
        flags = 0;
        streamId = 0;
        payload = [];

        Span<byte> hdr = stackalloc byte[9];
        if (!ReadFully(hdr))
        {
            return false;
        }

        int len = (hdr[0] << 16) | (hdr[1] << 8) | hdr[2];
        type = hdr[3];
        flags = hdr[4];
        streamId = ((hdr[5] & 0x7f) << 24) | (hdr[6] << 16) | (hdr[7] << 8) | hdr[8];

        payload = new byte[len];
        return len == 0 || ReadFully(payload);
    }

    private bool ReadFully(Span<byte> buf)
    {
        int off = 0;
        while (off < buf.Length)
        {
            int n;
            try
            {
                n = _s.Read(buf[off..]);
            }
            catch (IOException)
            {
                return false;   // read timeout or reset
            }
            if (n <= 0)
            {
                return false;
            }
            off += n;
        }
        return true;
    }

    // Literal header field without indexing, new name, no Huffman (RFC 7541 6.2.2): 0x00, then a
    // length-prefixed name and value. The four request pseudo-headers, in the required order.
    private static byte[] Hpack(string method, string path)
    {
        var buf = new List<byte>();

        void Literal(string name, string value)
        {
            buf.Add(0x00);
            buf.Add((byte)name.Length);
            buf.AddRange(Encoding.ASCII.GetBytes(name));
            buf.Add((byte)value.Length);
            buf.AddRange(Encoding.ASCII.GetBytes(value));
        }

        Literal(":method", method);
        Literal(":scheme", "http");
        Literal(":path", path);
        Literal(":authority", "chaos");
        return buf.ToArray();
    }

    public void Dispose()
    {
        _s.Dispose();
        _tcp.Dispose();
    }
}
