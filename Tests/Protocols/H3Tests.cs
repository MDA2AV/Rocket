using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using ioxide;
using ioxide.nghttp3;
using ioxide.ngtcp2;

namespace Ioxide.E2E;

/// <summary>
/// The full HTTP/3 stack end to end: the ioxide reactor runs ngtcp2 + the ioxide.nghttp3 (nghttp3)
/// layer behind a QuicHandle; the client is a bare ngtcp2 + nghttp3 driver over a real loopback
/// UDP socket (the shims' test-only client entry points). Handshake, SETTINGS/QPACK prefaces,
/// request and response all cross the wire encrypted.
/// </summary>
internal static class H3Tests
{
    public static void Register(Runner runner)
    {
        runner.Test("h3: GET request/response through ioxide.nghttp3 (loopback)", () =>
        {
            (string certPath, string keyPath) = TestCert.Ensure();
            using var engine = new QuicEngine(certPath, keyPath, cidLength: 8);

            (_, int udpPort) = TestServer.StartDatagram(
                onDatagram: null,
                quicFactory: engine.CreateFactory(),
                quicHandle: static (_, conn) => new Nghttp3Connection(conn).RunBufferedAsync(
                    static req => Nghttp3Response.Text($"hello {Encoding.ASCII.GetString(req.Path.Span)} via {Encoding.ASCII.GetString(req.Method.Span)}")));

            using var client = new H3TestClient("127.0.0.1", udpPort);
            client.Connect();
            Assert.True(client.CompleteHandshake(timeoutMs: 5000), "handshake did not complete");

            (int status, string body) = client.Get("/greet", timeoutMs: 5000);
            Assert.Equal(200, status);
            Assert.Equal("hello /greet via GET", body);
        });

        runner.Test("h3: streaming body upload (end-of-headers dispatch, paced window)", () =>
        {
            (string certPath, string keyPath) = TestCert.Ensure();
            using var engine = new QuicEngine(certPath, keyPath, cidLength: 8);

            // The async overload: dispatch fires at end-of-headers and the body is pulled through
            // BodyReader while the stream is flow-control paced.
            (_, int udpPort) = TestServer.StartDatagram(
                onDatagram: null,
                quicFactory: engine.CreateFactory(),
                quicHandle: static (_, conn) => new Nghttp3Connection(conn).RunStreamingAsync(
                    static async req =>
                    {
                        long total = 0;
                        while (true)
                        {
                            ReadOnlyMemory<byte> chunk = await req.BodyReader!.ReadAsync();
                            if (chunk.IsEmpty)
                            {
                                break;
                            }
                            total += chunk.Length;
                        }
                        return Nghttp3Response.Text($"got {total}");
                    }));

            using var client = new H3TestClient("127.0.0.1", udpPort);
            client.Connect();
            Assert.True(client.CompleteHandshake(timeoutMs: 5000), "handshake did not complete");

            // 600 KB > the 256 KB stream window: the transfer only completes if consumed bytes
            // are credited back mid-flight - this exercises the whole pacing/consume path.
            var body = new byte[600_000];
            new Random(7).NextBytes(body);

            (int status, string text) = client.Request("POST", "/upload", body, timeoutMs: 10_000);
            Assert.Equal(200, status);
            Assert.Equal("got 600000", text);
        });

        runner.Test("h3: buffered-async handler (whole body in req.Body, handler may await)", () =>
        {
            (string certPath, string keyPath) = TestCert.Ensure();
            using var engine = new QuicEngine(certPath, keyPath, cidLength: 8);

            (_, int udpPort) = TestServer.StartDatagram(
                onDatagram: null,
                quicFactory: engine.CreateFactory(),
                quicHandle: static (_, conn) => new Nghttp3Connection(conn).RunBufferedAsync(
                    static async req =>
                    {
                        // Dispatch fired at end-of-stream: the body is complete before we run.
                        await ValueTask.CompletedTask;   // where a db call would slot in
                        return Nghttp3Response.Text(
                            $"buffered got {req.Body.Length} first {req.Body.Span[0]} last {req.Body.Span[^1]}");
                    }));

            using var client = new H3TestClient("127.0.0.1", udpPort);
            client.Connect();
            Assert.True(client.CompleteHandshake(timeoutMs: 5000), "handshake did not complete");

            var body = new byte[200_000];
            new Random(21).NextBytes(body);

            (int status, string text) = client.Request("POST", "/upload", body, timeoutMs: 10_000);
            Assert.Equal(200, status);
            Assert.Equal($"buffered got 200000 first {body[0]} last {body[^1]}", text);
        });
        runner.Test("h3: QPACK dynamic table enabled (fat cookie decodes via insertions)", () =>
        {
            (string certPath, string keyPath) = TestCert.Ensure();
            using var engine = new QuicEngine(certPath, keyPath, cidLength: 8);

            var options = new Nghttp3Options
            {
                QpackDynamicTableCapacity = 4096,
                QpackBlockedStreams = 100,
            };

            // Handler echoes the cookie's length - proving the header survived the dynamic-table
            // encode/decode round trip (the client's nghttp3 encoder inserts eligible headers the
            // moment our SETTINGS advertise capacity).
            (_, int udpPort) = TestServer.StartDatagram(
                onDatagram: null,
                quicFactory: engine.CreateFactory(),
                quicHandle: (_, conn) => new Nghttp3Connection(conn, options).RunBufferedAsync(
                    static req =>
                    {
                        // Byte-level cookie lookup - no strings, and it sees cookie field lines
                        // however h3 split them.
                        return req.TryGetCookie("session"u8, out ReadOnlyMemory<byte> session)
                            ? Nghttp3Response.Text($"cookie {session.Length}")
                            : Nghttp3Response.Text("no cookie", status: 400);
                    }));

            using var client = new H3TestClient("127.0.0.1", udpPort);
            client.Connect();
            Assert.True(client.CompleteHandshake(timeoutMs: 5000), "handshake did not complete");

            // Two pairs in one field line: the parser must find "session" and stop at the ';'.
            string cookie = "session=" + new string('s', 400) + "; tracking=abc";
            (int status, string text) = client.Request("GET", "/", null,
                [("cookie", cookie)], timeoutMs: 5000);
            Assert.Equal(200, status);
            Assert.Equal("cookie 400", text);
        });

        runner.Test("h3: cookies split across multiple field lines (RFC 9114 4.2.1)", () =>
        {
            (string certPath, string keyPath) = TestCert.Ensure();
            using var engine = new QuicEngine(certPath, keyPath, cidLength: 8);

            // Echo every cookie found, in order - proves the enumerator walks BOTH field lines
            // (a Headers.TryGet("cookie") would only ever see the first).
            (_, int udpPort) = TestServer.StartDatagram(
                onDatagram: null,
                quicFactory: engine.CreateFactory(),
                quicHandle: static (_, conn) => new Nghttp3Connection(conn).RunBufferedAsync(
                    static req =>
                    {
                        var pairs = new List<string>();
                        foreach ((ReadOnlyMemory<byte> name, ReadOnlyMemory<byte> value) in req.Cookies)
                        {
                            pairs.Add($"{Encoding.ASCII.GetString(name.Span)}={Encoding.ASCII.GetString(value.Span)}");
                        }
                        return Nghttp3Response.Text(string.Join('|', pairs));
                    }));

            using var client = new H3TestClient("127.0.0.1", udpPort);
            client.Connect();
            Assert.True(client.CompleteHandshake(timeoutMs: 5000), "handshake did not complete");

            // Two separate cookie field lines, the second carrying two pairs - exactly the shape
            // h3 permits and h1 never produces.
            (int status, string text) = client.Request("GET", "/", null,
                [("cookie", "a=1"), ("cookie", "b=2; c=3")], timeoutMs: 5000);
            Assert.Equal(200, status);
            Assert.Equal("a=1|b=2|c=3", text);
        });
        runner.Test("h3: graceful shutdown (GOAWAY) still answers the in-flight request", () =>
        {
            (string certPath, string keyPath) = TestCert.Ensure();
            using var engine = new QuicEngine(certPath, keyPath, cidLength: 8);

            // The handler calls Shutdown() while ITS OWN request is in flight: the GOAWAY goes
            // out, new streams are refused, and this response must still reach the client.
            (_, int udpPort) = TestServer.StartDatagram(
                onDatagram: null,
                quicFactory: engine.CreateFactory(),
                quicHandle: static (_, conn) =>
                {
                    Nghttp3Connection h3 = new(conn);
                    return h3.RunBufferedAsync(req =>
                    {
                        h3.Shutdown();   // drain begins; this request completes normally
                        return Nghttp3Response.Text("bye after goaway");
                    });
                });

            using var client = new H3TestClient("127.0.0.1", udpPort);
            client.Connect();
            Assert.True(client.CompleteHandshake(timeoutMs: 5000), "handshake did not complete");

            (int status, string text) = client.Get("/last", timeoutMs: 5000);
            Assert.Equal(200, status);
            Assert.Equal("bye after goaway", text);
        });
    }
}

/// <summary>
/// A minimal HTTP/3 client for the test: QUIC via the ioxide.ngtcp2 shim's client entry points (like
/// QuicTestClient), H3 framing via the ioxide.nghttp3 shim's client conn. Not production code - it
/// exists purely to drive the server stack.
/// </summary>
internal sealed unsafe class H3TestClient : IDisposable
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
