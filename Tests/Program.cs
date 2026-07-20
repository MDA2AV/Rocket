using System.Net;
using System.Net.Sockets;
using System.Text;
using ioxide;
using ioxide.file;
using ioxide.pg;
using ioxide.redis;
using ioxide.tls;

namespace Ioxide.E2E;

/// <summary>
/// End-to-end suite: starts real ioxide servers and drives them over real sockets, asserting
/// behavior (not timings or throughput, so it isn't brittle). pg / redis / kTLS tests skip when the
/// dependency is unreachable. Exit code is non-zero if any test fails.
/// </summary>
internal static class Program
{
    private static int Main()
    {
        var runner = new Runner();

        (string Host, int Port) pg = (Env("EXAMPLES_PG_HOST", "127.0.0.1"), EnvInt("EXAMPLES_PG_PORT", 5432));
        (string Host, int Port) redis = (Env("EXAMPLES_REDIS_HOST", "127.0.0.1"), EnvInt("EXAMPLES_REDIS_PORT", 6379));

        bool pgUp = Sidecars.Reachable(pg.Host, pg.Port);
        bool redisUp = Sidecars.Reachable(redis.Host, redis.Port);
        bool ktls = Sidecars.KtlsAvailable();

        Console.WriteLine(
            $"sidecars: pg {(pgUp ? "up" : "down")} ({pg.Host}:{pg.Port}), " +
            $"redis {(redisUp ? "up" : "down")} ({redis.Host}:{redis.Port}), " +
            $"kTLS {(ktls ? "available" : "absent")}\n");

        // ---- core: reactor + connection + buffer ring (no sidecar) ----
        runner.Test("core: raw echo", () =>
        {
            int port = TestServer.Start(Handlers.Raw);
            (int status, string body) = Client.Get(port, "/");
            Assert.Equal(200, status);
            Assert.Equal("ok", body);
        });

        runner.Test("core: keep-alive (5 requests, one connection)", () =>
        {
            int port = TestServer.Start(Handlers.Raw);
            var replies = Client.GetKeepAlive(port, "/", 5);
            Assert.Equal(5, replies.Count);
            foreach ((int status, string body) in replies)
            {
                Assert.Equal(200, status);
                Assert.Equal("ok", body);
            }
        });

        runner.Test("core: 50 fresh connections (accept + recycle)", () =>
        {
            int port = TestServer.Start(Handlers.Raw);
            for (int i = 0; i < 50; i++)
            {
                (int status, _) = Client.Get(port, "/");
                Assert.Equal(200, status);
            }
        });

        // ---- core: udp transport (recvmsg slots + sendmsg-with-address) ----
        runner.Test("core: udp echo (3 datagrams, one socket)", () =>
        {
            (_, int udpPort) = TestServer.StartDatagram(
                static (Reactor r, in UdpDatagram d) => r.UdpSendTo(d.SocketFd, d.PeerAddr, d.PeerAddrLen, d.Payload));

            using var client = new UdpClient();
            client.Client.ReceiveTimeout = 4000;
            var server = new IPEndPoint(IPAddress.Loopback, udpPort);

            for (int i = 0; i < 3; i++)
            {
                byte[] ping = Encoding.ASCII.GetBytes($"ping-{i}");
                client.Send(ping, ping.Length, server);
                IPEndPoint? from = null;
                byte[] reply = client.Receive(ref from);
                Assert.Equal($"ping-{i}", Encoding.ASCII.GetString(reply));
            }
        });

        runner.Test("core: udp 8 KiB payload + two clients (per-datagram addressing)", () =>
        {
            (_, int udpPort) = TestServer.StartDatagram(
                static (Reactor r, in UdpDatagram d) => r.UdpSendTo(d.SocketFd, d.PeerAddr, d.PeerAddrLen, d.Payload));

            var server = new IPEndPoint(IPAddress.Loopback, udpPort);
            using var a = new UdpClient();
            using var b = new UdpClient();
            a.Client.ReceiveTimeout = 4000;
            b.Client.ReceiveTimeout = 4000;

            byte[] big = new byte[8192];
            Random.Shared.NextBytes(big);
            byte[] small = Encoding.ASCII.GetBytes("from-b");

            a.Send(big, big.Length, server);
            b.Send(small, small.Length, server);

            IPEndPoint? from = null;
            byte[] replyA = a.Receive(ref from);
            byte[] replyB = b.Receive(ref from);
            Assert.True(replyA.AsSpan().SequenceEqual(big), "8 KiB echo mismatch");
            Assert.Equal("from-b", Encoding.ASCII.GetString(replyB));
        });

        runner.Test("core: udp gso reply (UDP_SEGMENT splits one submit into wire datagrams)", () =>
        {
            (_, int udpPort) = TestServer.StartDatagram(
                static (Reactor r, in UdpDatagram d) => r.UdpSendTo(d.SocketFd, d.PeerAddr, d.PeerAddrLen, d.Payload, gsoSegmentSize: 4));

            using var client = new UdpClient();
            client.Client.ReceiveTimeout = 4000;
            var server = new IPEndPoint(IPAddress.Loopback, udpPort);
            IPEndPoint? from = null;

            byte[] batch = Encoding.ASCII.GetBytes("abcdefgh");
            client.Send(batch, batch.Length, server);
            Assert.Equal("abcd", Encoding.ASCII.GetString(client.Receive(ref from)));
            Assert.Equal("efgh", Encoding.ASCII.GetString(client.Receive(ref from)));
        });

        // ---- core: quic transport scaffold (DCID demux; no engine yet) ----
        runner.Test("core: quic dcid demux (long-header adopt, short-header route)", () =>
        {
            EchoQuicConnection.Created = 0;
            (_, int udpPort) = TestServer.StartDatagram(
                onDatagram: null,
                quicFactory: static (Reactor r, in UdpDatagram d, in QuicCid dcid) =>
                {
                    EchoQuicConnection.Created++;
                    var conn = new EchoQuicConnection();
                    r.QuicRegisterCid(conn, new QuicCid("srv-cid1"u8));   // the CID "we" minted
                    return conn;
                });

            using var client = new UdpClient();
            client.Client.ReceiveTimeout = 4000;
            var server = new IPEndPoint(IPAddress.Loopback, udpPort);
            IPEndPoint? from = null;

            // Long header: 0xC0 | version 1 | dcid len 8 | dcid | trailing bytes.
            byte[] initial = [0xC0, 0, 0, 0, 1, 8, .. "cli-cid1"u8, .. "hello"u8];
            client.Send(initial, initial.Length, server);
            Assert.True(client.Receive(ref from).AsSpan().SequenceEqual(initial), "long-header echo mismatch");

            // Same client DCID again - must route to the existing connection, not mint a second.
            client.Send(initial, initial.Length, server);
            client.Receive(ref from);

            // Short header: 0x40 | the server-minted 8-byte CID | payload.
            byte[] shortHdr = [0x40, .. "srv-cid1"u8, .. "ping"u8];
            client.Send(shortHdr, shortHdr.Length, server);
            Assert.True(client.Receive(ref from).AsSpan().SequenceEqual(shortHdr), "short-header echo mismatch");

            Assert.Equal(1, EchoQuicConnection.Created);
        });

        // ---- pg pool fails fast on a dead backend (#1) - needs NO live pg ----
        runner.Test("pg: dead backend fails fast, no hang (#1)", () =>
        {
            PgOptions dead = PgOpts(pg) with { Port = 5599 };
            int port = TestServer.Start(Handlers.Pg, r => PgPool.Start(r, dead));
            (int status, _) = Client.Get(port, "/", timeoutMs: 8000);
            Assert.Equal(500, status);   // PgException surfaced quickly, not a hang
        });

        // ---- pg (needs the sidecar) ----
        runner.Test("pg: SELECT 42", () =>
        {
            int port = TestServer.Start(Handlers.Pg, r => PgPool.Start(r, PgOpts(pg)));
            (int status, string body) = Client.Get(port, "/");
            Assert.Equal(200, status);
            Assert.Equal("42", body);
        }, skip: !pgUp);

        runner.Test("pg: prepared int parameter", () =>
        {
            int port = TestServer.Start(Handlers.Pg, r => PgPool.Start(r, PgOpts(pg)));
            (int status, string body) = Client.Get(port, "/add/41");
            Assert.Equal(200, status);
            Assert.Equal("42", body);
        }, skip: !pgUp);

        runner.Test("pg: row streaming", () =>
        {
            int port = TestServer.Start(Handlers.Pg, r => PgPool.Start(r, PgOpts(pg)));
            (int status, string body) = Client.Get(port, "/rows");
            Assert.Equal(200, status);
            Assert.Equal("rows=5", body);
        }, skip: !pgUp);

        runner.Test("pg: server error then connection stays usable", () =>
        {
            int port = TestServer.Start(Handlers.Pg, r => PgPool.Start(r, PgOpts(pg)));
            (int badStatus, string sqlState) = Client.Get(port, "/bad");
            Assert.Equal(500, badStatus);
            Assert.Equal("42P01", sqlState);   // undefined_table

            (int okStatus, string body) = Client.Get(port, "/");
            Assert.Equal(200, okStatus);
            Assert.Equal("42", body);
        }, skip: !pgUp);

        runner.Test("pg: command timeout (#2)", () =>
        {
            int port = TestServer.Start(Handlers.PgSlow, r => PgPool.Start(r, PgOpts(pg, commandTimeoutMs: 1000)));
            (int status, _) = Client.Get(port, "/slow", timeoutMs: 8000);
            Assert.Equal(503, status);
        }, skip: !pgUp);

        // ---- redis (needs the sidecar) ----
        runner.Test("redis: SET then GET", () =>
        {
            int port = TestServer.Start(Handlers.Redis, r => RedisPool.Start(r, RedisOpts(redis)));
            (int status, string body) = Client.Get(port, "/");
            Assert.Equal(200, status);
            Assert.Equal("hello", body);
        }, skip: !redisUp);

        runner.Test("redis: INCR (RESP integer)", () =>
        {
            int port = TestServer.Start(Handlers.Redis, r => RedisPool.Start(r, RedisOpts(redis)));
            (int status, string body) = Client.Get(port, "/incr");
            Assert.Equal(200, status);
            Assert.True(long.TryParse(body, out long n) && n >= 1, $"expected a positive integer, got [{body}]");
        }, skip: !redisUp);

        runner.Test("redis: pipeline SET/INCR/GET", () =>
        {
            int port = TestServer.Start(Handlers.Redis, r => RedisPool.Start(r, RedisOpts(redis)));
            (int status, string body) = Client.Get(port, "/pipe");
            Assert.Equal(200, status);
            Assert.Equal("2", body);
        }, skip: !redisUp);

        // ---- file (no sidecar) ----
        runner.Test("file: serve a baked asset", () =>
        {
            var assets = new StaticAssets(SampleAssets());
            int port = TestServer.Start(Handlers.Files, r =>
            {
                r.AddService(assets);
                AssetReader.CreatePool(r, readers: 2, bufferBytes: 64 * 1024);
            });
            (int status, string body) = Client.Get(port, "/hello.txt");
            Assert.Equal(200, status);
            Assert.Equal("hello-asset", body);
        });

        runner.Test("file: 404 on a miss", () =>
        {
            var assets = new StaticAssets(SampleAssets());
            int port = TestServer.Start(Handlers.Files, r =>
            {
                r.AddService(assets);
                AssetReader.CreatePool(r, readers: 2, bufferBytes: 64 * 1024);
            });
            (int status, _) = Client.Get(port, "/nope.txt");
            Assert.Equal(404, status);
        });

        // ---- tls (needs the kernel 'tls' module) ----
        runner.Test("tls: kTLS handshake + request", () =>
        {
            (string certPath, string keyPath) = TestCert.Ensure();
            var options = new TlsOptions { CertificatePath = certPath, KeyPath = keyPath };
            int port = TestServer.Start(Handlers.Tls, r => TlsService.Start(r, options));
            (int status, string body) = Client.GetTls(port, "/");
            Assert.Equal(200, status);
            Assert.Equal("tls-ok", body);
        }, skip: !ktls);

        return runner.Summary();
    }

    private static PgOptions PgOpts((string Host, int Port) pg, int commandTimeoutMs = 30_000) => new()
    {
        Host = pg.Host,
        Port = (ushort)pg.Port,
        User = Env("EXAMPLES_PG_USER", "bench"),
        Database = Env("EXAMPLES_PG_DB", "bench"),
        Password = Environment.GetEnvironmentVariable("EXAMPLES_PG_PASSWORD"),
        PoolSize = 2,
        CommandTimeoutMs = commandTimeoutMs,
    };

    private static RedisOptions RedisOpts((string Host, int Port) redis) => new()
    {
        Host = redis.Host,
        Port = (ushort)redis.Port,
        Password = Environment.GetEnvironmentVariable("EXAMPLES_REDIS_PASSWORD"),
        PoolSize = 2,
    };

    private static string SampleAssets()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ioxide-e2e-assets");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "hello.txt"), "hello-asset");
        return dir;
    }

    private static string Env(string key, string fallback) => Environment.GetEnvironmentVariable(key) ?? fallback;
    private static int EnvInt(string key, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(key), out int v) ? v : fallback;
}

/// <summary>Engine-less QUIC connection for demux tests: echoes each routed datagram to the peer.</summary>
internal sealed class EchoQuicConnection : QuicConnection
{
    public static int Created;

    public override void OnDatagram(ReadOnlySpan<byte> payload, byte tos, int groSegmentSize) => Send(payload);
    public override long GetNextTimeout(long nowMs) => long.MaxValue;
    public override void OnTimer(long nowMs) { }
    public override void OnEvicted(QuicEvictReason reason) { }
}

/// <summary>Tiny test runner: PASS / FAIL / SKIP per test, a summary line, and a non-zero exit on failure.</summary>
internal sealed class Runner
{
    private int _passed;
    private int _failed;
    private int _skipped;

    public void Test(string name, Action body, bool skip = false)
    {
        if (skip)
        {
            Console.WriteLine($"SKIP  {name}");
            _skipped++;
            return;
        }

        try
        {
            body();
            Console.WriteLine($"PASS  {name}");
            _passed++;
        }
        catch (Exception e)
        {
            Console.WriteLine($"FAIL  {name}: {e.Message}");
            _failed++;
        }
    }

    public int Summary()
    {
        Console.WriteLine($"\n{_passed} passed, {_failed} failed, {_skipped} skipped");
        return _failed == 0 ? 0 : 1;
    }
}

internal static class Assert
{
    public static void Equal<T>(T expected, T actual)
    {
        if (!Equals(expected, actual))
        {
            throw new Exception($"expected [{expected}], got [{actual}]");
        }
    }

    public static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception(message);
        }
    }
}
