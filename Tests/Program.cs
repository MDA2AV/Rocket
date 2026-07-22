using System.Net;
using System.Net.Sockets;
using System.Text;
using ioxide;
using ioxide.utils;
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

        // ---- core: hardening (issues #92/#93/#94) ----

        runner.Test("core: shared recv survives buffer-group exhaustion (#93)", () =>
        {
            const int totalBytes = 24 * 1024;   // vs an 8 x 1 KiB group: guaranteed exhaustion while held
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            (int port, _) = TestServer.StartConfigured(
                async (_, conn) =>
                {
                    try
                    {
                        long got = 0;
                        bool held = false;
                        while (true)
                        {
                            RecvSnapshot snapshot = await conn.ReadAsync();
                            UnmanagedMemoryManager[] rings = conn.GetSnapshotMemories(snapshot);
                            foreach (UnmanagedMemoryManager m in rings)
                            {
                                got += m.Memory.Length;
                            }

                            if (!held && rings.Length > 0)
                            {
                                held = true;
                                await release.Task;   // hold the first buffers while the client floods
                            }
                            conn.ReturnBuffers(rings);

                            if (got >= totalBytes)
                            {
                                conn.Write("done"u8);
                                await conn.FlushAsync();
                            }
                            if (snapshot.IsClosed)
                            {
                                return;
                            }
                            conn.ResetRead();
                        }
                    }
                    finally
                    {
                        conn.DecRef();
                    }
                },
                new ServerConfig
                {
                    RecvBufferSize = 1024, BufferRingEntries = 8,
                    WriteSlabSize = 4096, PoolMax = 8, RecvQueueEntries = 64,
                });

            using var client = new TcpClient();
            client.Connect("127.0.0.1", port);
            client.ReceiveTimeout = 8000;
            NetworkStream stream = client.GetStream();

            byte[] payload = new byte[totalBytes];
            stream.Write(payload, 0, 1024);                    // handler reads this and parks holding it
            Thread.Sleep(150);
            stream.Write(payload, 1024, totalBytes - 1024);    // floods past the 8-buffer group
            Thread.Sleep(300);                                 // unfixed core: -ENOBUFS teardown here
            release.SetResult();                               // buffers return; recv must resume

            byte[] reply = new byte[4];
            int n = stream.Read(reply, 0, 4);
            Assert.True(n == 4 && Encoding.ASCII.GetString(reply) == "done",
                "connection died during buffer-group exhaustion (expected it to stall and resume)");
        });

        runner.Test("core: incremental recv survives per-conn ring exhaustion (#93)", () =>
        {
            const int totalBytes = 12 * 1024;   // vs a 4 x 1 KiB per-conn ring
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            (int port, _) = TestServer.StartConfigured(
                async (_, conn) =>
                {
                    try
                    {
                        long got = 0;
                        bool held = false;
                        while (true)
                        {
                            RecvSnapshot snapshot = await conn.ReadAsync();
                            UnmanagedMemoryManager[] rings = conn.GetSnapshotMemories(snapshot);
                            foreach (UnmanagedMemoryManager m in rings)
                            {
                                got += m.Memory.Length;
                            }

                            if (!held && rings.Length > 0)
                            {
                                held = true;
                                await release.Task;
                            }
                            conn.ReturnBuffers(rings);

                            if (got >= totalBytes)
                            {
                                conn.Write("done"u8);
                                await conn.FlushAsync();
                            }
                            if (snapshot.IsClosed)
                            {
                                return;
                            }
                            conn.ResetRead();
                        }
                    }
                    finally
                    {
                        conn.DecRef();
                    }
                },
                new ServerConfig
                {
                    Incremental = true, MaxConnections = 8, ConnBufRingEntries = 4, IncRecvBufferSize = 1024,
                    WriteSlabSize = 4096, PoolMax = 8, RecvQueueEntries = 64,
                });

            using var client = new TcpClient();
            client.Connect("127.0.0.1", port);
            client.ReceiveTimeout = 8000;
            NetworkStream stream = client.GetStream();

            byte[] payload = new byte[totalBytes];
            stream.Write(payload, 0, 1024);
            Thread.Sleep(150);
            stream.Write(payload, 1024, totalBytes - 1024);
            Thread.Sleep(300);
            release.SetResult();

            byte[] reply = new byte[4];
            int n = stream.Read(reply, 0, 4);
            Assert.True(n == 4 && Encoding.ASCII.GetString(reply) == "done",
                "connection died during per-conn ring exhaustion (expected it to stall and resume)");
        }, skip: !TestServer.KernelAtLeast(6, 12));

        runner.Test("core: faulted handler releases the connection (#94)", () =>
        {
            (int port, _) = TestServer.StartConfigured(
                async (_, _) =>
                {
                    await Task.Yield();
                    throw new InvalidOperationException("boom (test)");
                },
                new ServerConfig
                {
                    RecvBufferSize = 1024, BufferRingEntries = 64,
                    WriteSlabSize = 4096, PoolMax = 8, RecvQueueEntries = 64,
                });

            Thread.Sleep(200);   // let WaitForListen's probe connection recycle before the baseline
            int before = Directory.EnumerateFileSystemEntries("/proc/self/fd").Count();

            for (int i = 0; i < 10; i++)
            {
                using var c = new TcpClient();
                c.Connect("127.0.0.1", port);
                c.GetStream().Write("x"u8.ToArray());
                Thread.Sleep(20);
            }   // dispose closes the client; the server must observe EOF and recycle

            Thread.Sleep(500);
            int leaked = Directory.EnumerateFileSystemEntries("/proc/self/fd").Count() - before;
            Assert.True(leaked <= 3, $"{leaked} fds leaked by 10 faulted handlers (CLOSE_WAIT sockets)");
        });

        runner.Test("core: incremental accept past MaxConnections sheds, reactor survives (#92)", () =>
        {
            (int port, _) = TestServer.StartConfigured(Handlers.Raw,
                new ServerConfig
                {
                    Incremental = true, MaxConnections = 4, ConnBufRingEntries = 4, IncRecvBufferSize = 1024,
                    WriteSlabSize = 4096, PoolMax = 8, RecvQueueEntries = 64,
                });

            Thread.Sleep(250);   // WaitForListen's probe connection must recycle and free its gid

            static void GetOk(TcpClient c)
            {
                NetworkStream s = c.GetStream();
                s.Write(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: t\r\n\r\n"));
                byte[] buf = new byte[256];
                int n = s.Read(buf, 0, buf.Length);
                Assert.True(n > 0 && Encoding.ASCII.GetString(buf, 0, n).Contains("200"),
                    "keep-alive connection stopped responding");
            }

            // Fill the reactor to its gid cap with live keep-alive connections.
            var held = new List<TcpClient>();
            for (int i = 0; i < 4; i++)
            {
                var c = new TcpClient();
                c.Connect("127.0.0.1", port);
                c.ReceiveTimeout = 4000;
                held.Add(c);
                GetOk(c);
            }

            // Beyond the cap: the unfixed core throws in AllocGid and the reactor dies here.
            for (int i = 0; i < 3; i++)
            {
                using var extra = new TcpClient();
                extra.Connect("127.0.0.1", port);
                extra.ReceiveTimeout = 1500;
                try
                {
                    _ = extra.GetStream().Read(new byte[1], 0, 1);   // shed = immediate close
                }
                catch
                {
                    // RST/timeout are both acceptable shed observations
                }
            }

            // The reactor must still serve the connections it already owns...
            GetOk(held[0]);

            // ...and accept fresh ones once capacity frees.
            held[3].Close();
            held.RemoveAt(3);
            Thread.Sleep(300);   // recycle returns the gid

            using var fresh = new TcpClient();
            fresh.Connect("127.0.0.1", port);
            fresh.ReceiveTimeout = 4000;
            GetOk(fresh);

            foreach (TcpClient c in held)
            {
                c.Close();
            }
        }, skip: !TestServer.KernelAtLeast(6, 12));

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
