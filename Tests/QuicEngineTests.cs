using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using ioxide;
using ioxide.ngtcp2;

namespace Ioxide.E2E;

/// <summary>
/// Full QUIC engine (ngtcp2 + picotls, bundled): a real TLS 1.3 handshake and an encrypted stream
/// echo end to end - the ioxide reactor runs the server engine, a bare ngtcp2 client (via the shim's
/// test-only client entry points) drives it over a real loopback UDP socket. No external QUIC stack.
/// </summary>
internal static class QuicEngineTests
{
    public static void Register(Runner runner)
    {
        runner.Test("quic: full ngtcp2 handshake + encrypted stream echo (loopback)", () =>
        {
            (string certPath, string keyPath) = TestCert.Ensure();
            using var engine = new QuicEngine(certPath, keyPath, cidLength: 8);

            (_, int udpPort) = TestServer.StartDatagram(
                onDatagram: null,
                quicFactory: engine.CreateFactory(),
                quicHandle: EchoHandler);

            using var client = new QuicTestClient("127.0.0.1", udpPort);
            client.Connect();

            // Pump the handshake to completion (each side answers the other's datagrams).
            Assert.True(client.CompleteHandshake(timeoutMs: 5000), "handshake did not complete");

            byte[] sent = Encoding.ASCII.GetBytes("hello-quic-echo");
            string echoed = client.RequestEcho(sent, timeoutMs: 5000);
            Assert.Equal("hello-quic-echo", echoed);
        });

        runner.Test("quic: dual-pipe (PipeReader/PipeWriter) stream echo (loopback)", () =>
        {
            (string certPath, string keyPath) = TestCert.Ensure();
            using var engine = new QuicEngine(certPath, keyPath, cidLength: 8);

            (_, int udpPort) = TestServer.StartDatagram(
                onDatagram: null,
                quicFactory: engine.CreateFactory(),
                quicHandle: PipeEchoHandler);

            using var client = new QuicTestClient("127.0.0.1", udpPort);
            client.Connect();
            Assert.True(client.CompleteHandshake(timeoutMs: 5000), "handshake did not complete");

            byte[] sent = Encoding.ASCII.GetBytes("hello-quic-pipe-echo");
            string echoed = client.RequestEcho(sent, timeoutMs: 5000);
            Assert.Equal("hello-quic-pipe-echo", echoed);
        });
    }

    // Server side, the dual-pipe model: auto-bind to the client's stream, echo until fin, then
    // Complete() half-closes with the server's own fin (which RequestEcho waits for).
    private static async Task PipeEchoHandler(Reactor reactor, QuicConnection conn)
    {
        try
        {
            var pipe = new QuicConnectionDualPipe(conn);

            while (true)
            {
                System.IO.Pipelines.ReadResult result = await pipe.Input.ReadAsync();

                foreach (ReadOnlyMemory<byte> segment in result.Buffer)
                {
                    pipe.Output.Write(segment.Span);
                }
                await pipe.Output.FlushAsync();

                pipe.Input.AdvanceTo(result.Buffer.End);

                if (result.IsCompleted)
                {
                    break;
                }
            }

            pipe.Output.Complete();
            pipe.Input.Complete();
        }
        finally
        {
            conn.DecRef();
        }
    }

    // Server side, the delegate model: await stream events, echo each back on its own stream.
    private static async Task EchoHandler(Reactor reactor, QuicConnection conn)
    {
        try
        {
            while (true)
            {
                QuicRecvSnapshot snap = await conn.ReadAsync();

                while (conn.TryGetDelivery(in snap, out QuicRecvRing.Delivery item))
                {
                    conn.SendStream(item.StreamId, item.AsSpan(), item.Fin);
                    conn.ReturnBuffer(in item);
                }

                if (snap.IsClosed)
                {
                    break;
                }
                conn.ResetRead();
            }
        }
        finally
        {
            conn.DecRef();
        }
    }
}

/// <summary>
/// A minimal ngtcp2 client for the test, using the shim's client entry points and a real loopback
/// UDP socket. Not production code - it exists purely to drive the server engine.
/// </summary>
internal sealed unsafe class QuicTestClient : IDisposable
{
    private readonly UdpClient _udp;
    private readonly IPEndPoint _server;
    private nint _clientEngine;
    private nint _conn;
    private long _streamId = -1;
    private readonly List<byte> _echo = [];
    private bool _echoFin;

    private static ulong NowNs() => (ulong)(System.Diagnostics.Stopwatch.GetTimestamp() *
                                            (1_000_000_000.0 / System.Diagnostics.Stopwatch.Frequency));

    public QuicTestClient(string host, int port)
    {
        _udp = new UdpClient();
        _udp.Client.ReceiveTimeout = 250;
        _server = new IPEndPoint(IPAddress.Parse(host), port);
        _udp.Connect(_server);   // fixes the local port so the server's replies come back here
    }

    public void Connect()
    {
        var cbs = new IqCallbacks
        {
            OnStreamData = &OnClientStreamData,
        };
        _clientEngine = iq_client_engine_new("echo", cbs);
        Assert.True(_clientEngine != 0, "client engine init failed");

        // Local + remote sockaddr_in (loopback). ngtcp2 needs both for the path.
        Span<byte> local = stackalloc byte[16];
        Span<byte> remote = stackalloc byte[16];
        FillSockaddrIn(local, (ushort)((IPEndPoint)_udp.Client.LocalEndPoint!).Port, IPAddress.Loopback);
        FillSockaddrIn(remote, (ushort)_server.Port, IPAddress.Loopback);

        fixed (byte* l = local)
        fixed (byte* r = remote)
        {
            _conn = iq_client_connect(_clientEngine, l, 16, r, 16, "localhost", "echo",
                                      NowNs(), (void*)GCHandle.ToIntPtr(GCHandle.Alloc(this)));
        }
        Assert.True(_conn != 0, "client connect failed");
    }

    // Drive the handshake: write client datagrams out, read server datagrams in, until established.
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

    // Open a bidi stream, send the payload with FIN, and collect the echoed bytes.
    public string RequestEcho(byte[] payload, int timeoutMs)
    {
        _streamId = iq_client_open_bidi(_conn);
        Assert.True(_streamId >= 0, "failed to open client stream");

        long consumed;
        fixed (byte* dest = _sendScratch)
        fixed (byte* src = payload)
        {
            // Loop the engine until the payload is fully accepted and its datagrams are sent.
            int off = 0;
            long sid = _streamId;
            while (true)
            {
                byte* dataPtr = sid >= 0 ? src + off : null;
                nuint dataLen = sid >= 0 ? (nuint)(payload.Length - off) : 0;
                nint n = iq_conn_write(_conn, dest, (nuint)_sendScratch.Length, sid,
                                       dataPtr, dataLen, 1, &consumed, NowNs());
                int code = (int)n;
                if (code < 0)
                {
                    sid = -1;   // stream done/blocked - keep flushing the connection's own packets
                    continue;
                }
                if (consumed > 0)
                {
                    off += (int)consumed;
                }
                if (n > 0)
                {
                    _udp.Send(_sendScratch, (int)n);
                }
                if (n == 0)
                {
                    if (sid >= 0 && off < payload.Length)
                    {
                        sid = -1;
                        continue;
                    }
                    break;
                }
            }
        }

        long deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline && !_echoFin)
        {
            FlushOut();
            PumpIn();
        }
        return Encoding.ASCII.GetString(_echo.ToArray());
    }

    private readonly byte[] _sendScratch = new byte[1452];

    // Drain the client engine's pending datagrams (ACKs, handshake) to the wire.
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

    // Read one server datagram (if any within the socket timeout) and feed it to the engine.
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
            // timeout - the engine timer may still need pumping; the caller loops
        }
    }

    private static void FillSockaddrIn(Span<byte> sa, ushort port, IPAddress addr)
    {
        sa.Clear();
        sa[0] = 2;   // AF_INET (x86 little-endian: family low byte)
        sa[2] = (byte)(port >> 8);
        sa[3] = (byte)(port & 0xff);
        addr.GetAddressBytes().CopyTo(sa[4..]);
    }

    [UnmanagedCallersOnly]
    private static void OnClientStreamData(void* user, long streamId, byte* data, nuint len, int fin)
    {
        var self = (QuicTestClient)GCHandle.FromIntPtr((nint)user).Target!;
        self._echo.AddRange(new ReadOnlySpan<byte>(data, (int)len).ToArray());
        if (fin != 0)
        {
            self._echoFin = true;
        }
    }

    public void Dispose()
    {
        if (_conn != 0) iq_conn_free(_conn);
        if (_clientEngine != 0) iq_client_engine_free(_clientEngine);
        _udp.Dispose();
    }

    // --- shim client entry points (test-only) ---

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

    private const string Lib = "ioxide_ngtcp2";
    [DllImport(Lib)] private static extern nint iq_client_engine_new([MarshalAs(UnmanagedType.LPUTF8Str)] string alpn, IqCallbacks cbs);
    [DllImport(Lib)] private static extern void iq_client_engine_free(nint e);
    [DllImport(Lib)] private static extern nint iq_client_connect(nint e, byte* localSa, nuint localLen, byte* remoteSa, nuint remoteLen, [MarshalAs(UnmanagedType.LPUTF8Str)] string serverName, [MarshalAs(UnmanagedType.LPUTF8Str)] string alpn, ulong ts, void* user);
    [DllImport(Lib)] private static extern long iq_client_open_bidi(nint conn);
    [DllImport(Lib)] private static extern nint iq_conn_write(nint conn, byte* dest, nuint destLen, long streamId, byte* data, nuint dataLen, int fin, long* pConsumed, ulong ts);
    [DllImport(Lib)] private static extern int  iq_conn_read(nint conn, void* remoteSa, nuint remoteLen, byte* pkt, nuint pktLen, byte ecn, ulong ts);
    [DllImport(Lib)] private static extern int  iq_conn_is_established(nint conn);
    [DllImport(Lib)] private static extern void iq_conn_free(nint conn);
}
