using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace Ioxide.Tests;

/// <summary>
/// A minimal HTTP/3 client for the test: QUIC via the ioxide.ngtcp2 shim's client entry points (like
/// QuicTestClient), H3 framing via the ioxide.nghttp3 shim's client conn. Not production code - it
/// exists purely to drive the server stack.
/// </summary>
public sealed unsafe class H3TestClient : IDisposable
{
    private readonly UdpClient _udp;
    private readonly IPEndPoint _server;
    private nint _clientEngine;
    private nint _conn;
    private nint _h3;
    private GCHandle _self;

    private long _requestSid = -1;
    private int _status = -1;
    private readonly List<byte> _body = [];
    private bool _done;

    private static ulong NowNs() => (ulong)(System.Diagnostics.Stopwatch.GetTimestamp() *
                                            (1_000_000_000.0 / System.Diagnostics.Stopwatch.Frequency));

    public H3TestClient(string host, int port)
    {
        _udp = new UdpClient();
        _udp.Client.ReceiveTimeout = 250;
        _server = new IPEndPoint(IPAddress.Parse(host), port);
        _udp.Connect(_server);
    }

    public void Connect()
    {
        _self = GCHandle.Alloc(this);

        var quicCbs = new IqCallbacks
        {
            OnStreamData = &OnQuicStreamData,
        };
        _clientEngine = iq_client_engine_new("h3", quicCbs);
        Assert.True(_clientEngine != 0, "client engine init failed");

        Span<byte> local = stackalloc byte[16];
        Span<byte> remote = stackalloc byte[16];
        FillSockaddrIn(local, (ushort)((IPEndPoint)_udp.Client.LocalEndPoint!).Port, IPAddress.Loopback);
        FillSockaddrIn(remote, (ushort)_server.Port, IPAddress.Loopback);

        fixed (byte* l = local)
        fixed (byte* r = remote)
        {
            _conn = iq_client_connect(_clientEngine, l, 16, r, 16, "localhost", "h3",
                                      0, NowNs(), (void*)GCHandle.ToIntPtr(_self), null);
        }
        Assert.True(_conn != 0, "client connect failed");
    }

    public bool CompleteHandshake(int timeoutMs)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            FlushOut();
            if (iq_conn_is_established(_conn) != 0)
            {
                return true;
            }
            PumpIn();
        }
        return false;
    }

    public (int Status, string Body) Get(string path, int timeoutMs)
        => Request("GET", path, null, timeoutMs);

    /// <summary>Submit one request (optional body, served through the shim's data reader) and
    /// pump until the response completes.</summary>
    public (int Status, string Body) Request(string method, string path, byte[]? body, int timeoutMs)
        => Request(method, path, body, extraHeaders: null, timeoutMs);

    public (int Status, string Body) Request(string method, string path, byte[]? body, (string Name, string Value)[]? extraHeaders, int timeoutMs)
    {
        // Client H3 conn + its control/QPACK uni streams.
        var h3Cbs = new Ih3Callbacks
        {
            OnBeginHeaders = &OnH3BeginHeaders,
            OnHeader       = &OnH3Header,
            OnEndHeaders   = &OnH3EndHeaders,
            OnData         = &OnH3Data,
            OnEndStream    = &OnH3EndStream,
        };
        _h3 = ih3_client_new(h3Cbs, (void*)GCHandle.ToIntPtr(_self));
        Assert.True(_h3 != 0, "h3 client conn init failed");

        long ctrl = iq_conn_open_uni(_conn);
        long qenc = iq_conn_open_uni(_conn);
        long qdec = iq_conn_open_uni(_conn);
        Assert.True(ctrl >= 0 && qenc >= 0 && qdec >= 0, "failed to open client uni streams");
        Assert.True(ih3_bind_streams(_h3, ctrl, qenc, qdec) == 0, "bind streams failed");

        _requestSid = iq_client_open_bidi(_conn);
        Assert.True(_requestSid >= 0, "failed to open request stream");

        // Let the server's SETTINGS land before encoding: with a dynamic-table capacity
        // advertised, the client encoder only uses it once it has SEEN that advertisement.
        for (int settle = 0; settle < 10; settle++)
        {
            DrainH3Out();
            FlushOut();
            PumpIn();
        }

        var headerList = new List<(string, string)>
        {
            (":method", method),
            (":scheme", "https"),
            (":authority", "localhost"),
            (":path", path),
        };
        if (extraHeaders is not null)
        {
            headerList.AddRange(extraHeaders.Select(h => (h.Name, h.Value)));
        }
        byte[] headers = PackHeaders([.. headerList]);
        fixed (byte* p = headers)
        fixed (byte* pb = body)
        {
            Assert.True(ih3_submit_request(_h3, _requestSid, p, (nuint)headers.Length,
                    pb, (nuint)(body?.Length ?? 0)) == 0,
                "submit_request failed");
        }

        long deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline && !_done)
        {
            DrainH3Out();
            FlushOut();
            PumpIn();
        }

        return (_status, Encoding.UTF8.GetString(_body.ToArray()));
    }

    // Pump the client H3 engine's egress (prefaces, the request) into the QUIC engine per stream.
    private readonly byte[] _h3Buf = new byte[16 * 1024];

    private void DrainH3Out()
    {
        fixed (byte* p = _h3Buf)
        {
            while (true)
            {
                long sid;
                int fin;
                long n = ih3_writev(_h3, &sid, &fin, p, (nuint)_h3Buf.Length);
                Assert.True(n >= 0, $"client h3 writev failed: {n}");
                if (n == 0 && sid == -1)
                {
                    return;
                }
                WriteStream(sid, _h3Buf.AsSpan(0, (int)n), fin != 0);
            }
        }
    }

    // The QuicTestClient write loop: feed one stream's bytes into ngtcp2, sending each produced
    // datagram, falling back to the generic flush when the stream can't take more.
    private readonly byte[] _sendScratch = new byte[1452];

    private void WriteStream(long sid, ReadOnlySpan<byte> data, bool fin)
    {
        // Never drop: the shim's ih3_writev already told nghttp3 these bytes are written
        // (add_write_offset), so a blocked tail must be retried, not abandoned. While blocked
        // (flow-control window / cwnd full) pump the wire - the server's consume credits arrive
        // as datagrams, and processing them is what reopens our window (the streaming-upload
        // backpressure path exercises exactly this).
        long deadline = Environment.TickCount64 + 10_000;
        int off = 0;
        bool finPending = fin;

        while (off < data.Length || finPending)
        {
            Assert.True(Environment.TickCount64 < deadline, "client write stalled (window never reopened)");

            long consumed;
            nint n;
            fixed (byte* dest = _sendScratch)
            fixed (byte* src = data)
            {
                byte* ptr = off < data.Length ? src + off : null;
                n = iq_conn_write(_conn, dest, (nuint)_sendScratch.Length, sid,
                                  ptr, (nuint)(data.Length - off), finPending ? 1 : 0, &consumed, NowNs());
            }

            if ((int)n < 0)
            {
                FlushOut();   // engine frames (ACKs etc.) still flow while the stream is blocked
                PumpIn();
                continue;
            }

            if (consumed > 0)
            {
                off += (int)consumed;
                if (off >= data.Length)
                {
                    finPending = false;   // the fin flag rode out with the final bytes
                }
            }
            else if (finPending && off >= data.Length && n > 0)
            {
                finPending = false;       // bare-fin frame went out
            }

            if (n > 0)
            {
                _udp.Send(_sendScratch, (int)n);
            }
            else if (consumed <= 0)
            {
                FlushOut();   // engine can't take more now - pump until credits arrive
                PumpIn();
            }
        }
    }

    private void FlushOut()
    {
        long consumed;
        fixed (byte* dest = _sendScratch)
        {
            while (true)
            {
                nint n = iq_conn_write(_conn, dest, (nuint)_sendScratch.Length, -1, null, 0, 0, &consumed, NowNs());
                if (n <= 0)
                {
                    break;
                }
                _udp.Send(_sendScratch, (int)n);
            }
        }
    }

    private void PumpIn()
    {
        try
        {
            IPEndPoint? from = null;
            byte[] pkt = _udp.Receive(ref from);
            fixed (byte* p = pkt)
            {
                iq_conn_read(_conn, null, 0, p, (nuint)pkt.Length, 0, NowNs());
            }
        }
        catch (SocketException)
        {
            // timeout - the caller loops
        }
    }

    private static byte[] PackHeaders((string Name, string Value)[] headers)
    {
        var buf = new MemoryStream(128);
        Span<byte> len = stackalloc byte[2];
        foreach ((string name, string value) in headers)
        {
            byte[] n = Encoding.ASCII.GetBytes(name);
            byte[] v = Encoding.ASCII.GetBytes(value);
            BitConverter.TryWriteBytes(len, (ushort)n.Length);
            buf.Write(len);
            buf.Write(n);
            BitConverter.TryWriteBytes(len, (ushort)v.Length);
            buf.Write(len);
            buf.Write(v);
        }
        return buf.ToArray();
    }

    private static void FillSockaddrIn(Span<byte> sa, ushort port, IPAddress addr)
    {
        sa.Clear();
        sa[0] = 2;
        sa[2] = (byte)(port >> 8);
        sa[3] = (byte)(port & 0xff);
        addr.GetAddressBytes().CopyTo(sa[4..]);
    }

    private static H3TestClient From(void* user)
        => (H3TestClient)GCHandle.FromIntPtr((nint)user).Target!;

    // QUIC stream bytes in -> client H3 conn.
    [UnmanagedCallersOnly]
    private static void OnQuicStreamData(void* user, long streamId, byte* data, nuint len, int fin)
    {
        H3TestClient self = From(user);
        if (self._h3 == 0)
        {
            return;
        }
        long rv = ih3_read_stream(self._h3, streamId, data, len, fin);
        if (rv < 0)
        {
            self._done = true;   // surfaces as a failed assert on status/body
        }
    }

    [UnmanagedCallersOnly]
    private static void OnH3BeginHeaders(void* user, long streamId) { }

    [UnmanagedCallersOnly]
    private static void OnH3Header(void* user, long streamId, byte* name, nuint nameLen, byte* value, nuint valueLen)
    {
        H3TestClient self = From(user);
        string n = Encoding.ASCII.GetString(name, (int)nameLen);
        if (n == ":status")
        {
            self._status = int.Parse(Encoding.ASCII.GetString(value, (int)valueLen));
        }
    }

    [UnmanagedCallersOnly]
    private static void OnH3EndHeaders(void* user, long streamId, int fin) { }

    [UnmanagedCallersOnly]
    private static void OnH3Data(void* user, long streamId, byte* data, nuint len)
    {
        H3TestClient self = From(user);
        if (streamId == self._requestSid)
        {
            self._body.AddRange(new ReadOnlySpan<byte>(data, (int)len).ToArray());
        }
    }

    [UnmanagedCallersOnly]
    private static void OnH3EndStream(void* user, long streamId)
    {
        H3TestClient self = From(user);
        if (streamId == self._requestSid)
        {
            self._done = true;
        }
    }

    public void Dispose()
    {
        if (_h3 != 0) ih3_free(_h3);
        if (_conn != 0) iq_conn_free(_conn);
        if (_clientEngine != 0) iq_client_engine_free(_clientEngine);
        if (_self.IsAllocated) _self.Free();
        _udp.Dispose();
    }

    // --- shim entry points (test-only client surfaces) ---

    [StructLayout(LayoutKind.Sequential)]
    private struct IqCallbacks
    {
        public delegate* unmanaged<void*, long, byte*, nuint, int, void> OnStreamData;
        public delegate* unmanaged<void*, long, ulong, void>            OnStreamClose;
        public delegate* unmanaged<void*, void>                         OnHandshakeCompleted;
        public delegate* unmanaged<void*, byte*, nuint, void>           OnNewCid;
        public delegate* unmanaged<void*, byte*, nuint, void>           OnRetireCid;
        public delegate* unmanaged<void*, long, ulong, void>            OnStreamReset;
        public delegate* unmanaged<void*, long, ulong, void>            OnStreamStopSending;
        public delegate* unmanaged<void*, long, ulong, ulong, void>     OnAckedStreamData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Ih3Callbacks
    {
        public delegate* unmanaged<void*, long, void>                          OnBeginHeaders;
        public delegate* unmanaged<void*, long, byte*, nuint, byte*, nuint, void> OnHeader;
        public delegate* unmanaged<void*, long, int, void>                     OnEndHeaders;
        public delegate* unmanaged<void*, long, byte*, nuint, void>            OnData;
        public delegate* unmanaged<void*, long, void>                          OnEndStream;
        public delegate* unmanaged<void*, long, nuint, void>                   OnDeferredConsume;
    }

    private const string QuicLib = "ioxide_ngtcp2";
    [DllImport(QuicLib)] private static extern nint iq_client_engine_new([MarshalAs(UnmanagedType.LPUTF8Str)] string alpn, IqCallbacks cbs);
    [DllImport(QuicLib)] private static extern void iq_client_engine_free(nint e);
    [DllImport(QuicLib)] private static extern nint iq_client_connect(nint e, byte* localSa, nuint localLen, byte* remoteSa, nuint remoteLen, [MarshalAs(UnmanagedType.LPUTF8Str)] string serverName, [MarshalAs(UnmanagedType.LPUTF8Str)] string alpn, nuint scidLen, ulong ts, void* user, byte* scidOut);
    [DllImport(QuicLib)] private static extern long iq_client_open_bidi(nint conn);
    [DllImport(QuicLib)] private static extern long iq_conn_open_uni(nint conn);
    [DllImport(QuicLib)] private static extern nint iq_conn_write(nint conn, byte* dest, nuint destLen, long streamId, byte* data, nuint dataLen, int fin, long* pConsumed, ulong ts);
    [DllImport(QuicLib)] private static extern int  iq_conn_read(nint conn, void* remoteSa, nuint remoteLen, byte* pkt, nuint pktLen, byte ecn, ulong ts);
    [DllImport(QuicLib)] private static extern int  iq_conn_is_established(nint conn);
    [DllImport(QuicLib)] private static extern void iq_conn_free(nint conn);

    private const string H3Lib = "ioxide_nghttp3";
    [DllImport(H3Lib)] private static extern nint ih3_client_new(Ih3Callbacks cbs, void* user);
    [DllImport(H3Lib)] private static extern void ih3_free(nint conn);
    [DllImport(H3Lib)] private static extern int  ih3_bind_streams(nint conn, long ctrl, long qenc, long qdec);
    [DllImport(H3Lib)] private static extern long ih3_read_stream(nint conn, long streamId, byte* data, nuint dataLen, int fin);
    [DllImport(H3Lib)] private static extern int  ih3_submit_request(nint conn, long streamId, byte* headers, nuint headersLen, byte* body, nuint bodyLen);
    [DllImport(H3Lib)] private static extern long ih3_writev(nint conn, long* streamId, int* fin, byte* buf, nuint bufLen);
}
