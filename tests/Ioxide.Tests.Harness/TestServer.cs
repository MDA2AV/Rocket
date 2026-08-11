using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using ioxide;

namespace Ioxide.Tests;

/// <summary>
/// Starts an ioxide server on a unique loopback port and waits for it to listen. Most test servers
/// run on a background thread until the process exits; teardown-focused tests use the returned
/// reactor + thread to Stop() and join. Tiny buffers keep many concurrent test servers cheap.
/// </summary>
public static class TestServer
{
    /// <summary>
    /// Every test process needs its own port range, and the failure mode when it does not have one
    /// is silent rather than loud: ioxide listeners set SO_REUSEPORT, so two processes binding the
    /// same port BOTH succeed and the kernel load-balances connections between them. WaitForListen
    /// connects happily and a test is then answered by the other process's server, which shows up
    /// as an unrelated assertion failing somewhere far away.
    ///
    /// The window therefore mixes the entry assembly name (so different suites never overlap) with
    /// the process id (so two runs of the SAME suite - a developer and CI, or two terminals - do
    /// not either). No registration, no coordination, and new suites need no changes.
    /// </summary>
    private static int _nextPort = PortBaseForThisProcess();

    // 20000 to 32700, which stops short of ip_local_port_range (32768 on Linux by default): a
    // listener bound above that line races the machine's own ephemeral allocations, including the
    // client sockets these very tests open.
    private const int WindowSize = 100;
    private const int WindowCount = 127;

    private static int PortBaseForThisProcess()
    {
        string name = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name ?? "unknown";

        // Hashed by hand: string.GetHashCode is randomized per process, so the suite half of the
        // key would move between runs and stop being a property of the suite at all.
        int hash = 17;
        foreach (char c in name)
        {
            hash = (hash * 31) + c;
        }

        int window = Math.Abs(hash ^ Environment.ProcessId) % WindowCount;

        // 20000 upward, clear of bench/run.sh (18080-18464) and the playground defaults.
        return 20000 + (window * WindowSize);
    }

    /// <summary>Reserve a unique port (e.g. for TcpOptions.ExtraPorts).</summary>
    public static int NextPort() => Interlocked.Increment(ref _nextPort);

    /// <summary>
    /// A port with nothing listening on it, for the tests that need a connect to be refused - dead
    /// backends, connect-storm budgets, black-holed h3 endpoints.
    /// </summary>
    /// <remarks>
    /// Derived rather than hardcoded. A literal like 5599 or 9099 is a standing bet that no other
    /// process on the machine listens there, and losing that bet does not fail the test loudly - it
    /// inverts it, because the connect succeeds and the refusal being asserted never happens.
    /// Binding port 0 has the kernel pick one that was free a moment ago, and closing it
    /// immediately leaves it free.
    /// </remarks>
    public static int DeadPort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }

    /// <summary>The UDP flavour: nothing bound, so datagrams to it go nowhere.</summary>
    public static int DeadUdpPort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }

    public static int Start(Func<Reactor, TcpConnection, Task> handle, Action<Reactor>? onStart = null)
        => StartConfigured(handle, DefaultConfig(), onStart).Port;

    /// <summary>
    /// Start with explicit config overrides (Port and ReactorCount are stamped by the harness) and
    /// hand back the reactor + its thread so tests can assert against them or stop cleanly.
    /// </summary>
    public static (int Port, Reactor Reactor, Thread Thread) StartConfigured(
        Func<Reactor, TcpConnection, Task> handle, ServerConfig config, Action<Reactor>? onStart = null)
    {
        int port = Interlocked.Increment(ref _nextPort);
        config = config with { Tcp = (config.Tcp ?? new TcpOptions()) with { Port = (ushort)port }, ReactorCount = 1 };

        var reactor = new Reactor(0, config)
        {
            OnStart = onStart,
            TcpHandle = handle,
        };

        var thread = new Thread(RunGuarded(reactor, port))
        {
            IsBackground = true,
            Name = $"test-reactor-{port}",
        };
        thread.Start();

        WaitForListen(port);
        return (port, reactor, thread);
    }

    private static ServerConfig DefaultConfig() => new()
    {
        RecvBufferSize = 4096,
        RecvSlots = 256,
        Tcp = new TcpOptions
        {
            WriteSlabSize = 16 * 1024,
            PoolMax = 64,
            RecvQueueEntries = 64,
        },
    };

    /// <summary>
    /// Start <paramref name="reactorCount"/> reactors sharing ONE port via SO_REUSEPORT - the
    /// production sharding shape, which the single-reactor Start() helpers deliberately avoid. The
    /// handler is handed its reactor's shard index so a test can see which shard served each
    /// connection (and thus that the kernel spread connections across them).
    ///
    /// Readiness is gated on every shard's OnStart firing, not on a single probe: Run() opens the
    /// listener before OnStart, so once all N have signalled, all N listeners are bound and the
    /// kernel has the full set to load-balance across. Without that, a test racing ahead could open
    /// every connection before the later shards bound and see a false "no distribution".
    /// </summary>
    public static int StartSharded(int reactorCount, Func<int, Reactor, TcpConnection, Task> handle)
    {
        int port = Interlocked.Increment(ref _nextPort);
        var config = new ServerConfig
        {
            ReactorCount = reactorCount,
            RecvBufferSize = 4096,
            RecvSlots = 256,
            Tcp = new TcpOptions
            {
                Port = (ushort)port,
                WriteSlabSize = 16 * 1024,
                PoolMax = 64,
                RecvQueueEntries = 64,
            },
        };

        using var ready = new CountdownEvent(reactorCount);

        for (int i = 0; i < reactorCount; i++)
        {
            int shard = i;
            var reactor = new Reactor(shard, config)
            {
                TcpHandle = (r, conn) => handle(shard, r, conn),
                OnStart = _ => ready.Signal(),
            };

            var thread = new Thread(RunGuarded(reactor, port))
            {
                IsBackground = true,
                Name = $"test-shard-{port}-{shard}",
            };
            thread.Start();
        }

        // All shards up - or a shard died before OnStart, in which case WaitForListen surfaces the
        // real reason instead of a bare timeout.
        if (!ready.Wait(10_000))
        {
            WaitForListen(port);
        }

        return port;
    }

    /// <summary>Incremental mode (IOU_PBUF_RING_INC) needs 6.12+; tests skip below that.</summary>
    public static bool KernelAtLeast(int major, int minor)
    {
        Version v = Environment.OSVersion.Version;
        return v.Major > major || (v.Major == major && v.Minor >= minor);
    }

    /// <summary>
    /// Starts a reactor with a UDP port (plain datagram handler, or the QUIC transport when a
    /// factory is given). The TCP listener stays up solely so WaitForListen can probe readiness -
    /// by the time it accepts, the UDP recv slots (armed earlier in Run) are live.
    /// </summary>
    public static (int TcpPort, int UdpPort) StartDatagram(
        UdpDatagramHandler? onDatagram,
        QuicConnectionFactory? quicFactory = null,
        int quicIdleMs = 60_000,
        Func<Reactor, QuicConnection, Task>? quicHandle = null)
        => StartDatagramConfigured(onDatagram, quicFactory, quicIdleMs, udpRecvSlots: 16, quicHandle);

    /// <summary>StartDatagram with a tunable UDP ring depth (for the -ENOBUFS re-arm burst test).</summary>
    public static (int TcpPort, int UdpPort) StartDatagramConfigured(
        UdpDatagramHandler? onDatagram,
        QuicConnectionFactory? quicFactory = null,
        int quicIdleMs = 60_000,
        int udpRecvSlots = 16,
        Func<Reactor, QuicConnection, Task>? quicHandle = null)
    {
        int tcpPort = Interlocked.Increment(ref _nextPort);
        int udpPort = Interlocked.Increment(ref _nextPort);

        var config = new ServerConfig
        {
            ReactorCount = 1,
            RecvBufferSize = 4096,
            RecvSlots = 256,
            Tcp = new TcpOptions
            {
                Port = (ushort)tcpPort,
                WriteSlabSize = 16 * 1024,
                PoolMax = 64,
                RecvQueueEntries = 64,
            },
            Udp = new UdpOptions
            {
                RecvSlots = udpRecvSlots,
                Ports = quicFactory == null ? [(ushort)udpPort] : [],
            },
            Quic = quicFactory == null ? null : new QuicOptions
            {
                Port = (ushort)udpPort,
                LocalCidLength = 8,
                ConnectionFactory = quicFactory,
                IdleTimeoutMs = quicIdleMs,
            },
        };

        var reactor = new Reactor(0, config)
        {
            TcpHandle = static (_, _) => Task.CompletedTask,
            QuicHandle = quicHandle,
            OnDatagram = onDatagram,
        };

        var thread = new Thread(RunGuarded(reactor, tcpPort))
        {
            IsBackground = true,
            Name = $"test-reactor-udp-{udpPort}",
        };
        thread.Start();

        WaitForListen(tcpPort);
        return (tcpPort, udpPort);
    }

    /// <summary>
    /// A server with a TCP handler and NO QUIC configuration whatsoever - the standalone-client
    /// shape. Outbound HTTP/3 connections make the reactor open its own ephemeral-port socket on
    /// first use, so being an h3 client needs no listener, no fixed port and no accept factory.
    /// </summary>
    public static int StartQuicClientHost(Func<Reactor, TcpConnection, Task> tcpHandle, Action<Reactor> onStart)
    {
        int tcpPort = Interlocked.Increment(ref _nextPort);

        var config = new ServerConfig
        {
            ReactorCount = 1,
            RecvBufferSize = 4096,
            RecvSlots = 256,
            Tcp = new TcpOptions
            {
                Port = (ushort)tcpPort,
                WriteSlabSize = 16 * 1024,
                PoolMax = 64,
                RecvQueueEntries = 64,
            },
            Udp = new UdpOptions { RecvSlots = 16 },
            // No Quic block at all: the client opens its own socket when it first connects.
        };

        var reactor = new Reactor(0, config)
        {
            TcpHandle = tcpHandle,
            OnStart = onStart,
        };

        var thread = new Thread(RunGuarded(reactor, tcpPort))
        {
            IsBackground = true,
            Name = $"test-reactor-h3client-{tcpPort}",
        };
        thread.Start();

        WaitForListen(tcpPort);
        return tcpPort;
    }

    /// <summary>
    /// A driver whose reactor BOTH serves QUIC (accept factory + h3 handler) and makes outbound
    /// HTTP/3 calls - the proxy shape, where client connections share the configured QUIC socket
    /// rather than opening one.
    /// </summary>
    public static int StartQuicServingH3Driver(
        Func<Reactor, TcpConnection, Task> tcpHandle, QuicConnectionFactory quicFactory, Action<Reactor> onStart)
    {
        int tcpPort = Interlocked.Increment(ref _nextPort);
        int udpPort = Interlocked.Increment(ref _nextPort);

        var config = new ServerConfig
        {
            ReactorCount = 1,
            RecvBufferSize = 4096,
            RecvSlots = 256,
            Tcp = new TcpOptions
            {
                Port = (ushort)tcpPort,
                WriteSlabSize = 16 * 1024,
                PoolMax = 64,
                RecvQueueEntries = 64,
            },
            Udp = new UdpOptions { RecvSlots = 16 },
            Quic = new QuicOptions
            {
                Port = (ushort)udpPort,
                LocalCidLength = 8,
                ConnectionFactory = quicFactory,   // this reactor accepts QUIC too
            },
        };

        var reactor = new Reactor(0, config)
        {
            TcpHandle = tcpHandle,
            OnStart = onStart,
        };

        var thread = new Thread(RunGuarded(reactor, tcpPort))
        {
            IsBackground = true,
            Name = $"test-reactor-h3proxy-{tcpPort}",
        };
        thread.Start();

        WaitForListen(tcpPort);
        return tcpPort;
    }

    // Startup failures recorded by RunGuarded, keyed by the port that failed, so WaitForListen can
    // report the real reason instead of "never started listening".
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, Exception> StartupFailures = new();

    /// <summary>
    /// Reactor.Run as a thread body, with the exception caught. Without this a bind or listen
    /// failure is unhandled on a background thread and .NET terminates the process - so a single
    /// unlucky port produces ZERO test results rather than one FAIL, which is the worst possible
    /// way to learn about it in CI.
    /// </summary>
    private static ThreadStart RunGuarded(Reactor reactor, int port) => () =>
    {
        try
        {
            reactor.Run();
        }
        catch (Exception e)
        {
            StartupFailures[port] = e;

            // ALWAYS print, even though WaitForListen may also report it. Only failures that happen
            // before the listener is up are ever consumed there, and Run opens the listener before
            // InitSharedRingBuffer, OpenWakeFd and OnStart - so a reactor that dies in any of those
            // loses the race, WaitForListen's probe succeeds against the backlog, and the real
            // cause is never seen. Worse, a reactor dying AFTER its own test has passed would leave
            // the suite green, where before this guard existed it was a loud process kill. Catching
            // the exception must not make it quieter than not catching it.
            Console.Error.WriteLine($"[test-reactor:{port}] died: {e}");
        }
    };

    /// <summary>
    /// Reactor deaths that no WaitForListen consumed, cleared as they are read. A reactor can die
    /// long after its own test passed - in a ticker, a sweep, a later handler - and nothing else in
    /// the harness would ever notice, so the runner checks this before reporting success.
    /// </summary>
    public static IReadOnlyList<string> DrainUnreportedFailures()
    {
        var drained = new List<string>();
        foreach (int port in StartupFailures.Keys)
        {
            if (StartupFailures.TryRemove(port, out Exception? failure))
            {
                drained.Add($":{port} - {failure.Message}");
            }
        }
        return drained;
    }

    private static void WaitForListen(int port)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            if (StartupFailures.TryRemove(port, out Exception? failure))
            {
                throw new Exception($"server on :{port} failed to start: {failure.Message}", failure);
            }

            try
            {
                using var probe = new TcpClient();
                probe.Connect("127.0.0.1", port);
                return;
            }
            catch
            {
                Thread.Sleep(50);
            }
        }

        throw new Exception($"server on :{port} never started listening");
    }
}

/// <summary>A minimal HTTP/1.1 client over a raw socket (and over TLS), used to drive the servers.</summary>
public static class Client
{
    public static (int Status, string Body) Get(int port, string path, int timeoutMs = 6000)
    {
        using var client = new TcpClient();
        client.Connect("127.0.0.1", port);
        client.ReceiveTimeout = timeoutMs;

        NetworkStream stream = client.GetStream();
        Send(stream, path);
        return ReadResponse(stream);
    }

    public static (int Status, string Body) GetTls(int port, string path, int timeoutMs = 6000)
    {
        using var client = new TcpClient();
        client.Connect("127.0.0.1", port);
        client.ReceiveTimeout = timeoutMs;

        using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false, (_, _, _, _) => true);
        ssl.AuthenticateAsClient(new SslClientAuthenticationOptions
        {
            TargetHost = "localhost",
            EnabledSslProtocols = SslProtocols.Tls13,
        });

        Send(ssl, path);
        return ReadResponse(ssl);
    }

    /// <summary>
    /// Split ONE request across several TLS records - each SslStream.Write emits its own complete
    /// record - and report how many responses came back.
    ///
    /// This is the other fragmentation axis and the one that actually distinguishes handlers.
    /// A record split across TCP is all-or-nothing: OpenSSL yields nothing until it is whole, so
    /// even a handler that answers per decrypt sees exactly one decrypt. Split the REQUEST across
    /// records and each one decrypts on its own, so answering per decrypt answers several times.
    /// </summary>
    public static int CountTlsResponsesForMultiRecordRequest(int port, string path, int records,
        int settleMs = 1200, int timeoutMs = 15000)
    {
        using var client = new TcpClient();
        client.NoDelay = true;
        client.Connect("127.0.0.1", port);
        client.ReceiveTimeout = timeoutMs;

        using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: true, (_, _, _, _) => true);
        ssl.AuthenticateAsClient(new SslClientAuthenticationOptions
        {
            TargetHost = "localhost",
            EnabledSslProtocols = SslProtocols.Tls13,
        });

        byte[] request = Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: test\r\n\r\n");
        int piece = Math.Max(1, request.Length / records);

        for (int offset = 0; offset < request.Length; offset += piece)
        {
            ssl.Write(request, offset, Math.Min(piece, request.Length - offset));
            ssl.Flush();
            Thread.Sleep(30);   // let each record be its own recv on the server
        }

        Thread.Sleep(settleMs);
        client.ReceiveTimeout = 600;

        var seen = new System.Text.StringBuilder();
        byte[] buffer = new byte[8192];
        try
        {
            while (true)
            {
                int n = ssl.Read(buffer, 0, buffer.Length);
                if (n <= 0) break;
                seen.Append(Encoding.ASCII.GetString(buffer, 0, n));
            }
        }
        catch (IOException)
        {
        }

        int count = 0, at = 0;
        string text = seen.ToString();
        while ((at = text.IndexOf("HTTP/1.1 200", at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at += 12;
        }
        return count;
    }

    /// <summary>
    /// Like <see cref="GetTlsSplitRecords"/>, but reports how many complete responses came back for
    /// ONE request. A handler that answers per decrypt rather than per request replies more than
    /// once when the request arrives in pieces.
    /// </summary>
    public static int CountTlsResponsesForSplitRequest(int port, string path, int chunk,
        int settleMs = 1200, int timeoutMs = 15000)
    {
        using var client = new TcpClient();
        client.NoDelay = true;
        client.Connect("127.0.0.1", port);
        client.ReceiveTimeout = timeoutMs;

        using var chunking = new ChunkingStream(client.GetStream(), chunk);
        using var ssl = new SslStream(chunking, leaveInnerStreamOpen: true, (_, _, _, _) => true);
        ssl.AuthenticateAsClient(new SslClientAuthenticationOptions
        {
            TargetHost = "localhost",
            EnabledSslProtocols = SslProtocols.Tls13,
        });

        Send(ssl, path);

        // Let every response the server intends to send arrive before counting.
        Thread.Sleep(settleMs);
        client.ReceiveTimeout = 600;

        var seen = new System.Text.StringBuilder();
        byte[] buffer = new byte[8192];
        try
        {
            while (true)
            {
                int n = ssl.Read(buffer, 0, buffer.Length);
                if (n <= 0) break;
                seen.Append(Encoding.ASCII.GetString(buffer, 0, n));
            }
        }
        catch (IOException)
        {
            // read timeout - everything the server sent is already in `seen`
        }

        int count = 0, at = 0;
        while ((at = seen.ToString().IndexOf("HTTP/1.1 200", at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at += 12;
        }
        return count;
    }

    /// <summary>
    /// Send a request over TLS whose <b>TLS records are split across TCP writes</b>.
    ///
    /// The distinction matters and is easy to get wrong: SslStream.Write emits one COMPLETE record
    /// per call, so chunking at the SslStream level only produces many small whole records - which
    /// exercises nothing, because every recv then carries entire records. Chunking must happen
    /// BELOW TLS, so a single record arrives in pieces and the server has to hold partial record
    /// state across recvs. With chunk = 1 every byte of every record is its own TCP write.
    /// </summary>
    public static (int Status, string Body) GetTlsSplitRecords(int port, string path, int chunk,
        int timeoutMs = 15000)
    {
        using var client = new TcpClient();
        client.NoDelay = true;
        client.Connect("127.0.0.1", port);
        client.ReceiveTimeout = timeoutMs;

        using var chunking = new ChunkingStream(client.GetStream(), chunk);
        using var ssl = new SslStream(chunking, leaveInnerStreamOpen: true, (_, _, _, _) => true);
        ssl.AuthenticateAsClient(new SslClientAuthenticationOptions
        {
            TargetHost = "localhost",
            EnabledSslProtocols = SslProtocols.Tls13,
        });

        Send(ssl, path);
        return ReadResponse(ssl);
    }

    /// <summary>Splits every write into <paramref name="chunk"/>-byte writes, each flushed.</summary>
    private sealed class ChunkingStream(Stream inner, int chunk) : Stream
    {
        public override void Write(byte[] buffer, int offset, int count)
            => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            for (int i = 0; i < buffer.Length; i += chunk)
            {
                inner.Write(buffer.Slice(i, Math.Min(chunk, buffer.Length - i)));
                inner.Flush();

                // The pause is the point, not politeness. TCP_NODELAY stops the SENDER batching,
                // but the receiver still coalesces whatever has arrived into one recv - so without
                // a gap the server drains the whole record in a single completion and never holds
                // partial state. Sleeping lets the reactor's recv land mid-record, which is the
                // only thing that exercises reassembly.
                if (i + chunk < buffer.Length)
                {
                    Thread.Sleep(1);
                }
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override void Flush() => inner.Flush();
        public override bool CanRead => inner.CanRead;
        public override bool CanWrite => inner.CanWrite;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }


    /// <summary>
    /// Complete a TLS handshake, then write bytes that are not a valid TLS record. The server's
    /// decrypt must surface that as a FAULT rather than as "the record is incomplete, wait for
    /// more" - a corrupted stream that looks like a quiet connection leaves the reader hanging on
    /// one that is never going to recover.
    /// </summary>
    public static void SendTlsGarbageAfterHandshake(int port, int timeoutMs = 6000)
    {
        using var client = new TcpClient();
        client.Connect("127.0.0.1", port);
        client.ReceiveTimeout = timeoutMs;

        using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false, (_, _, _, _) => true);
        ssl.AuthenticateAsClient(new SslClientAuthenticationOptions
        {
            TargetHost = "localhost",
            EnabledSslProtocols = SslProtocols.Tls13,
        });

        // Underneath the SslStream, straight onto the socket: a record header claiming a content
        // type and version no TLS 1.3 peer will accept, followed by noise.
        NetworkStream raw = client.GetStream();
        byte[] bogus = [0x17, 0x03, 0x03, 0x00, 0x10, .. Enumerable.Repeat((byte)0xAB, 16)];
        raw.Write(bogus);
        raw.Flush();

        // Let the server read and fault before the socket closes under it, so what it reports is
        // the bad record rather than the disconnect.
        Thread.Sleep(500);
    }

    // Several requests over one connection (lock-step), to exercise the handler's keep-alive loop.
    public static List<(int Status, string Body)> GetKeepAlive(int port, string path, int count, int timeoutMs = 6000)
    {
        using var client = new TcpClient();
        client.Connect("127.0.0.1", port);
        client.ReceiveTimeout = timeoutMs;

        NetworkStream stream = client.GetStream();
        var results = new List<(int, string)>(count);
        for (int i = 0; i < count; i++)
        {
            Send(stream, path);
            results.Add(ReadResponse(stream));
        }

        return results;
    }

    public static void Send(Stream stream, string path)
    {
        stream.Write(Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: test\r\n\r\n"));
    }

    // Read the status line and the Content-Length body in full.
    public static (int Status, string Body) ReadResponse(Stream stream)
    {
        var buffer = new byte[64 * 1024];
        int filled = 0;
        int headerEnd = -1;

        while (headerEnd < 0)
        {
            int n = stream.Read(buffer, filled, buffer.Length - filled);
            if (n <= 0)
            {
                throw new Exception("connection closed before headers arrived");
            }

            filled += n;
            headerEnd = new ReadOnlySpan<byte>(buffer, 0, filled).IndexOf("\r\n\r\n"u8);
        }

        string head = Encoding.ASCII.GetString(buffer, 0, headerEnd);
        int status = int.Parse(head.AsSpan(9, 3));   // "HTTP/1.1 NNN ..."
        int contentLength = ContentLength(head);
        int bodyStart = headerEnd + 4;

        while (filled - bodyStart < contentLength)
        {
            int n = stream.Read(buffer, filled, buffer.Length - filled);
            if (n <= 0)
            {
                break;
            }
            filled += n;
        }

        string body = Encoding.ASCII.GetString(buffer, bodyStart, Math.Min(contentLength, filled - bodyStart));
        return (status, body);
    }

    private static int ContentLength(string head)
    {
        foreach (string line in head.Split("\r\n"))
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                return int.Parse(line.AsSpan("Content-Length:".Length).Trim());
            }
        }
        return 0;
    }
}

/// <summary>Reachability checks so sidecar-backed tests skip cleanly when the dependency is absent.</summary>
public static class Sidecars
{
    public static bool Reachable(string host, int port)
    {
        try
        {
            using var probe = new TcpClient();
            probe.Connect(host, port);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // kTLS needs the kernel's 'tls' module; the loaded module shows up here.
    public static bool KtlsAvailable() => Directory.Exists("/sys/module/tls");
}

/// <summary>A throwaway self-signed cert for the TLS test, written to PEM (ioxide.tls wants paths).</summary>
public static class TestCert
{
    /// <summary>
    /// A CA, a server certificate and two client certificates - one the CA signed, one a DIFFERENT
    /// CA signed. mTLS cannot be tested without all four: proving a good certificate is let in says
    /// nothing unless a bad one is turned away.
    /// </summary>
    public static (string CaPath, string ServerCert, string ServerKey,
                   string ClientCert, string ClientKey,
                   string RogueCert, string RogueKey) EnsureMutualTls()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ioxide-e2e-mtls");
        Directory.CreateDirectory(dir);

        string ca = Path.Combine(dir, "ca.crt");
        string serverCert = Path.Combine(dir, "server.crt");
        string serverKey = Path.Combine(dir, "server.key");
        string clientCert = Path.Combine(dir, "client.crt");
        string clientKey = Path.Combine(dir, "client.key");
        string rogueCert = Path.Combine(dir, "rogue.crt");
        string rogueKey = Path.Combine(dir, "rogue.key");

        if (File.Exists(ca) && File.Exists(clientCert) && File.Exists(rogueCert))
        {
            return (ca, serverCert, serverKey, clientCert, clientKey, rogueCert, rogueKey);
        }

        using var caKey = System.Security.Cryptography.RSA.Create(2048);
        var caRequest = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=ioxide test CA", caKey, System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(
            new System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension(true, false, 0, true));
        using var caCert = caRequest.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        File.WriteAllText(ca, caCert.ExportCertificatePem());

        Sign(caCert, "CN=localhost", serverCert, serverKey, server: true);
        Sign(caCert, "CN=alice", clientCert, clientKey, server: false);

        // A second CA the server has never heard of, so its certificates must be refused.
        using var rogueKeyPair = System.Security.Cryptography.RSA.Create(2048);
        var rogueCa = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=rogue CA", rogueKeyPair, System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        rogueCa.CertificateExtensions.Add(
            new System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension(true, false, 0, true));
        using var rogueCaCert = rogueCa.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        Sign(rogueCaCert, "CN=mallory", rogueCert, rogueKey, server: false);

        return (ca, serverCert, serverKey, clientCert, clientKey, rogueCert, rogueKey);

        static void Sign(System.Security.Cryptography.X509Certificates.X509Certificate2 issuer,
            string subject, string certPath, string keyPath, bool server)
        {
            using var key = System.Security.Cryptography.RSA.Create(2048);
            var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(
                subject, key, System.Security.Cryptography.HashAlgorithmName.SHA256,
                System.Security.Cryptography.RSASignaturePadding.Pkcs1);

            if (server)
            {
                var names = new System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder();
                names.AddDnsName("localhost");
                names.AddIpAddress(System.Net.IPAddress.Loopback);
                request.CertificateExtensions.Add(names.Build());
            }

            request.CertificateExtensions.Add(
                new System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension(
                    [new System.Security.Cryptography.Oid(server ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2")], false));

            byte[] serial = new byte[8];
            System.Security.Cryptography.RandomNumberGenerator.Fill(serial);
            using var signed = request.Create(issuer, DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddYears(1), serial);

            File.WriteAllText(certPath, signed.ExportCertificatePem());
            File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
        }
    }

    public static (string CertPath, string KeyPath) Ensure()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ioxide-e2e-tls");
        Directory.CreateDirectory(dir);

        string certPath = Path.Combine(dir, "test.crt");
        string keyPath = Path.Combine(dir, "test.key");

        if (!File.Exists(certPath))
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            // A subjectAltName, because client-side hostname verification checks that and only
            // falls back to the CN when no dNSName is present at all. Without it the certificate
            // is fine for a server that never gets verified and useless for testing a client that
            // does.
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName("localhost");
            names.AddIpAddress(System.Net.IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());

            using X509Certificate2 cert = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

            File.WriteAllText(certPath, cert.ExportCertificatePem());
            File.WriteAllText(keyPath, rsa.ExportPkcs8PrivateKeyPem());
        }

        return (certPath, keyPath);
    }
}
