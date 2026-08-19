using System.Net;
using System.Net.Sockets;
using ioxide.nghttp3;
using ioxide.ngtcp2;

namespace Ioxide.Tests;

/// <summary>
/// A client whose address changes mid-connection - the thing QUIC's connection id exists for.
/// </summary>
/// <remarks>
/// The shape is h2o's <c>40http3-migration.t</c> and nginx's <c>quic_migration.t</c>: put a UDP
/// forwarder between client and server, then swap the forwarder's UPSTREAM socket part way
/// through. The server sees the same connection id arriving from a new source address, exactly as
/// it would when a NAT rebinds a mapping or a phone moves from Wi-Fi to cellular.
///
/// That trigger is more ordinary than "the user changed network": home and mobile NATs recycle UDP
/// mappings after fairly short idle periods, so a connection that goes quiet and speaks again can
/// come back from a different port without the client moving at all.
///
/// What must happen is ngtcp2's decision, not ours - it challenges the new path, waits for the
/// response, and only then adopts it. What ioxide has to get right is feeding it the address the
/// datagram actually arrived on, and then sending to the address it settled on.
/// </remarks>
internal static class QuicMigrationTests
{
    public static void Register(Runner runner)
    {
        runner.Test("quic/migration: a client whose address changes keeps being served", () =>
        {
            (string certPath, string keyPath) = TestCert.Ensure();
            using var engine = new QuicEngine(certPath, keyPath, cidLength: 8, alpn: ["h3"]);

            (_, int serverPort) = TestServer.StartDatagram(
                onDatagram: null,
                quicFactory: engine.CreateFactory(),
                quicHandle: static (_, conn) => new Nghttp3Connection(conn).RunBufferedAsync(
                    static _ => Nghttp3Response.Text("migrated-ok")));

            using var forwarder = new UdpForwarder(serverPort);
            using var client = new H3TestClient("127.0.0.1", forwarder.Port);

            client.Connect();
            Assert.True(client.CompleteHandshake(10_000), "the handshake through the forwarder did not complete");

            // Before: proves the path under test is a working one, so a failure after the swap is
            // the swap and not the forwarder.
            (int firstStatus, string firstBody) = client.Request("GET", "/before", null, 10_000);
            Assert.Equal(200, firstStatus);
            Assert.Equal("migrated-ok", firstBody);

            // The client's address changes. Same connection id, new source port.
            forwarder.SwapUpstream();

            (int secondStatus, string secondBody) = client.Request("GET", "/after", null, 15_000);

            Assert.Equal(200, secondStatus);
            Assert.Equal("migrated-ok", secondBody);
            // The discriminating assertion, and the reason the two above are not enough. A server
            // that ignores the address change still answers requests it received BEFORE the swap,
            // so "the request succeeded" proves nothing on its own. What only a migrated server can
            // do is send to the new address - measured here as datagrams arriving back on the
            // socket the swap created. Without the path being passed through to ngtcp2 this is 0.
            Assert.True(forwarder.RelayedAfterSwap > 0,
                $"nothing was relayed after the swap, so the exchange never crossed the new path");
            Assert.True(forwarder.FromServerAfterSwap > 0,
                "the server never sent anything to the client's NEW address: it kept answering the "
                + "address the connection was accepted on, which is the connection blackholing");
        });

        runner.Test("control: the same exchange through a forwarder that never swaps", () =>
        {
            // Without this, the test above is satisfied by a forwarder that works and a migration
            // that never happened - and it would also hide a forwarder too slow to relay in time.
            (string certPath, string keyPath) = TestCert.Ensure();
            using var engine = new QuicEngine(certPath, keyPath, cidLength: 8, alpn: ["h3"]);

            (_, int serverPort) = TestServer.StartDatagram(
                onDatagram: null,
                quicFactory: engine.CreateFactory(),
                quicHandle: static (_, conn) => new Nghttp3Connection(conn).RunBufferedAsync(
                    static _ => Nghttp3Response.Text("migrated-ok")));

            using var forwarder = new UdpForwarder(serverPort);
            using var client = new H3TestClient("127.0.0.1", forwarder.Port);

            client.Connect();
            Assert.True(client.CompleteHandshake(10_000), "the handshake through the forwarder did not complete");

            Assert.Equal(200, client.Request("GET", "/one", null, 10_000).Status);
            Assert.Equal(200, client.Request("GET", "/two", null, 10_000).Status);
        });
    }

    /// <summary>
    /// Relays UDP between one client and one server, and can change the socket it uses towards the
    /// server - which is what the server sees as its peer moving. Deliberately a single thread:
    /// the interesting behaviour is on the server, and a forwarder with its own concurrency bugs
    /// would be indistinguishable from the defect under test.
    /// </summary>
    private sealed class UdpForwarder : IDisposable
    {
        private readonly Socket _front;               // faces the client
        private readonly IPEndPoint _server;
        private readonly Thread _pump;
        private volatile bool _running = true;
        private volatile Socket _upstream;            // faces the server; swapped mid-connection
        private readonly object _swapGate = new();
        private EndPoint? _client;
        private int _relayed;
        private int _fromServerAfterSwap;

        public int Port { get; }
        public int SwappedAt { get; private set; }
        public int RelayedAfterSwap { get; private set; }
        public int FromServerAfterSwap => _fromServerAfterSwap;

        public UdpForwarder(int serverPort)
        {
            _server = new IPEndPoint(IPAddress.Loopback, serverPort);

            _front = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _front.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            Port = ((IPEndPoint)_front.LocalEndPoint!).Port;

            _upstream = NewUpstream();

            _pump = new Thread(Pump) { IsBackground = true, Name = "udp-forwarder" };
            _pump.Start();
        }

        /// <summary>
        /// Change the source address the server sees, NOW. Deliberately synchronous: doing it
        /// lazily on the next relayed datagram let the request under test complete before the swap
        /// ever happened, and the test then passed against a server with no migration support at
        /// all - it was measuring nothing.
        /// </summary>
        public void SwapUpstream()
        {
            lock (_swapGate)
            {
                Socket replacement = NewUpstream();
                Socket old = _upstream;
                SwappedAt = _relayed;
                _upstream = replacement;
                old.Dispose();   // the pump's in-flight receive throws and retries on the new one
            }
        }

        private static Socket NewUpstream()
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));   // a fresh source port
            return socket;
        }

        private void Pump()
        {
            byte[] buffer = new byte[2048];
            _front.ReceiveTimeout = 50;

            while (_running)
            {
                Socket upstream = _upstream;
                upstream.ReceiveTimeout = 50;

                // client -> server
                try
                {
                    EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                    int n = _front.ReceiveFrom(buffer, ref from);
                    _client = from;

                    _upstream.SendTo(buffer, 0, n, SocketFlags.None, _server);
                    _relayed++;
                    if (SwappedAt > 0)
                    {
                        RelayedAfterSwap++;
                    }
                }
                catch (SocketException)
                {
                    // timeout, or the socket was swapped from under the receive - both ordinary
                }
                catch (ObjectDisposedException)
                {
                }

                // server -> client
                try
                {
                    EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                    int n = _upstream.ReceiveFrom(buffer, ref from);
                    if (_client is not null)
                    {
                        _front.SendTo(buffer, 0, n, SocketFlags.None, _client);
                        if (SwappedAt > 0) { _fromServerAfterSwap++; }
                    }
                }
                catch (SocketException)
                {
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        public void Dispose()
        {
            _running = false;
            _pump.Join(2_000);
            _front.Dispose();
            _upstream.Dispose();
        }
    }
}
