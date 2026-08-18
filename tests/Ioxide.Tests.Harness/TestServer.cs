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

    // Every server this harness starts, so the runner can shut them down when a test ends.
    private static readonly List<(Reactor Reactor, Thread Thread)> Started = [];
    private static readonly Lock StartedLock = new();

    private static void Track(Reactor reactor, Thread thread)
    {
        lock (StartedLock)
        {
            Started.Add((reactor, thread));
        }
    }

    /// <summary>
    /// Stops every server started since the last call. The runner does this after each test, so a
    /// suite runs against the servers the current test started and nothing else.
    /// </summary>
    /// <remarks>
    /// Not tidiness. A reactor polls its io_uring ring, so a server left running keeps a core busy
    /// for the whole suite; a hundred tests later the box is oversubscribed by two orders of
    /// magnitude and everything is slow. That shows up as unrelated tests timing out - which reads
    /// like a server bug and is only ever the harness competing with itself.
    ///
    /// Stopping is best-effort: a reactor that will not come down in time is left to the process
    /// exit rather than failing the test that happened to run last.
    /// </remarks>
    public static void StopAll()
    {
        (Reactor Reactor, Thread Thread)[] running;

        lock (StartedLock)
        {
            running = [.. Started];
            Started.Clear();
        }

        foreach ((Reactor reactor, _) in running)
        {
            try
            {
                reactor.Stop();
            }
            catch (Exception)
            {
                // Already stopped by the test itself, or never finished starting.
            }
        }

        int stuck = 0;

        foreach ((_, Thread thread) in running)
        {
            if (!thread.Join(2000))
            {
                stuck++;
            }
        }

        if (stuck > 0)
        {
            Console.WriteLine($"[harness] {stuck}/{running.Length} reactors did not stop");
        }
    }

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

        // Wrapped so the caller can be told when OnStart has actually RUN - see the wait below.
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var reactor = new Reactor(0, config)
        {
            OnStart = r =>
            {
                try
                {
                    onStart?.Invoke(r);
                    started.TrySetResult();
                }
                catch (Exception e)
                {
                    started.TrySetException(e);
                    throw;   // RunGuarded still records the reactor death, exactly as before
                }
            },
            TcpHandle = handle,
        };

        var thread = new Thread(RunGuarded(reactor, port))
        {
            IsBackground = true,
            Name = $"test-reactor-{port}",
        };
        thread.Start();
        Track(reactor, thread);

        WaitForListen(port);
        WaitForOnStart(port, started);
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
            Track(reactor, thread);
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
        Track(reactor, thread);

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
        Track(reactor, thread);

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
        Track(reactor, thread);

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

    /// <summary>
    /// Waits until the reactor's <c>OnStart</c> has finished, and rethrows what it threw.
    /// </summary>
    /// <remarks>
    /// Run opens the listener BEFORE it calls OnStart, so WaitForListen's probe proves only that
    /// the port is bound. Two things follow, and both were live:
    ///
    /// A test could return from Start and immediately use something OnStart was supposed to have
    /// created - a TlsService to rotate certificates on, most often - and get a null reference,
    /// intermittently, depending on which side won.
    ///
    /// And an OnStart that THREW left its exception in StartupFailures with nothing consuming it,
    /// so a test asserting that a bad configuration is refused could sail past its own assertion
    /// and have the refusal surface later as an unrelated "a test reactor died" at the end of the
    /// run. Every refusal test in the suite depended on losing that race the right way round.
    /// </remarks>
    private static void WaitForOnStart(int port, TaskCompletionSource started)
    {
        for (int attempt = 0; attempt < 200; attempt++)
        {
            // A reactor that died before reaching OnStart never completes the task, so its failure
            // has to be looked for rather than waited on.
            if (StartupFailures.TryRemove(port, out Exception? failure))
            {
                throw new Exception($"server on :{port} failed to start: {failure.Message}", failure);
            }

            try
            {
                if (started.Task.Wait(50))
                {
                    return;
                }
            }
            catch (AggregateException e) when (e.InnerException is not null)
            {
                StartupFailures.TryRemove(port, out _);   // consumed here, so it is not also reported unowned
                throw new Exception($"server on :{port} failed to start: {e.InnerException.Message}", e.InnerException);
            }
        }

        throw new Exception($"server on :{port} bound its listener but never finished OnStart");
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
    /// Handshakes asking for one host by name, and reports the certificate the server answered
    /// with - which is the whole observable effect of SNI selection.
    /// </summary>
    /// <param name="host">
    /// Sent as the SNI extension. Null sends none at all, which is how "a client that does not
    /// speak SNI gets the default certificate" is driven - SslStream omits the extension for an
    /// empty target host, as an IP literal is not a legal SNI value.
    /// </param>
    public static string ServerCertificateSubject(int port, string? host, int timeoutMs = 6000)
    {
        using var client = new TcpClient();
        client.Connect("127.0.0.1", port);
        client.ReceiveTimeout = timeoutMs;

        using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false, (_, _, _, _) => true);

        ssl.AuthenticateAsClient(new SslClientAuthenticationOptions
        {
            TargetHost = host ?? string.Empty,
            EnabledSslProtocols = SslProtocols.Tls13,
        });

        return ssl.RemoteCertificate?.Subject ?? "<none>";
    }

    /// <summary>
    /// Asks for one host by name and then USES the connection: reports the certificate served, the
    /// ALPN agreed, and the answer to a request over it.
    /// </summary>
    /// <remarks>
    /// The request is the point. Reading the certificate proves only that selection picked the
    /// right one - a host context missing something the connection needs afterwards, its keylog
    /// callback under kTLS being the real example, hands back a perfect certificate on a connection
    /// that then dies. Only asking it for something catches that.
    /// </remarks>
    public static (string Subject, string Alpn, int Status, string Body) GetTlsSni(
        int port, string path, string? host, string[]? alpn = null, int timeoutMs = 6000)
    {
        using var client = new TcpClient();
        client.Connect("127.0.0.1", port);
        client.ReceiveTimeout = timeoutMs;

        using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false, (_, _, _, _) => true);

        var options = new SslClientAuthenticationOptions
        {
            TargetHost = host ?? string.Empty,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        };

        if (alpn is not null)
        {
            options.ApplicationProtocols = [.. alpn.Select(a => new SslApplicationProtocol(a))];
        }

        ssl.AuthenticateAsClient(options);

        string subject = ssl.RemoteCertificate?.Subject ?? "<none>";
        string negotiated = ssl.NegotiatedApplicationProtocol.Protocol.Length == 0
            ? ""
            : ssl.NegotiatedApplicationProtocol.ToString();

        Send(ssl, path);
        (int status, string body) = ReadResponse(ssl);

        return (subject, negotiated, status, body);
    }

    /// <summary>
    /// Like <see cref="GetTls"/>, but presenting a client certificate - mutual TLS. Pass null to
    /// present none, which is how "the server demanded one and we had nothing" is driven.
    /// </summary>
    /// <remarks>
    /// SslStream is the client here on purpose: a passing test means ioxide's server agrees with an
    /// independent implementation rather than only with itself. A refused handshake surfaces as an
    /// <see cref="AuthenticationException"/> or an <see cref="IOException"/> depending on which side
    /// noticed first, so callers assert on "it threw" rather than on which one.
    /// </remarks>
    /// <param name="host">
    /// The name to ask for, sent as SNI. Defaults to the name the default certificate carries; pass
    /// another to drive a client certificate and a named host TOGETHER, which is what proves that
    /// selecting a certificate by name cannot also select its way out of client verification.
    /// </param>
    /// <summary>
    /// How an attempt to be served ended. The distinction is the point: a test asserting "the
    /// server refused this client" is satisfied, by a bare try/catch, by the server hanging, by the
    /// server crashing, and by the port being bound by something else entirely - so the assertion
    /// passes while the behaviour it names is broken.
    /// </summary>
    public enum TlsOutcome
    {
        /// <summary>Served. The handshake completed and the request was answered.</summary>
        Served,

        /// <summary>Refused with a TLS alert. The only outcome that means the server said no.</summary>
        Refused,

        /// <summary>Went away without an alert - dropped, reset, or the far side died.</summary>
        Dropped,

        /// <summary>Nothing came back in time. A server that hangs has not refused anything.</summary>
        TimedOut,
    }

    /// <summary>
    /// Attempts a full TLS request and classifies the outcome instead of merely reporting that
    /// something threw.
    /// </summary>
    /// <remarks>
    /// The request matters, and is not incidental. Under TLS 1.3 the client's Certificate is sent
    /// after the server's Finished, so a server rejecting it has nothing left to interrupt -
    /// AuthenticateAsClient returns happily and the alert only arrives when the client next reads.
    /// A helper that watched the handshake alone would report every rejected client certificate as
    /// a success.
    /// </remarks>
    public static TlsOutcome TryGetTls(int port, string path, string? certPath, string? keyPath,
        int timeoutMs = 6000, string host = "localhost")
    {
        try
        {
            (int status, _) = GetTlsClientCert(port, path, certPath, keyPath, timeoutMs, host);
            return status > 0 ? TlsOutcome.Served : TlsOutcome.Dropped;
        }
        catch (AuthenticationException)
        {
            return TlsOutcome.Refused;
        }
        catch (Exception e) when (e is IOException && Inner<AuthenticationException>(e) is not null)
        {
            return TlsOutcome.Refused;
        }
        catch (Exception e) when (Inner<SocketException>(e) is { SocketErrorCode: SocketError.TimedOut })
        {
            return TlsOutcome.TimedOut;
        }
        catch (IOException)
        {
            return TlsOutcome.Dropped;
        }
        catch (Exception e) when (e.Message.Contains("closed before headers", StringComparison.Ordinal))
        {
            return TlsOutcome.Dropped;
        }

        static T? Inner<T>(Exception e) where T : Exception
        {
            for (Exception? at = e; at is not null; at = at.InnerException)
            {
                if (at is T match)
                {
                    return match;
                }
            }
            return null;
        }
    }

    public static (int Status, string Body) GetTlsClientCert(
        int port, string path, string? certPath, string? keyPath, int timeoutMs = 6000,
        string host = "localhost")
    {
        using var client = new TcpClient();
        client.Connect("127.0.0.1", port);
        client.ReceiveTimeout = timeoutMs;

        var certificates = new X509CertificateCollection();
        if (certPath is not null && keyPath is not null)
        {
            using X509Certificate2 pem = X509Certificate2.CreateFromPemFile(certPath, keyPath);

            // SslStream on Linux needs the key associated through a PFX round-trip; a PEM-built
            // certificate carries it in a form the handshake will not use directly.
            certificates.Add(X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pfx), null));
        }

        using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false, (_, _, _, _) => true);
        ssl.AuthenticateAsClient(new SslClientAuthenticationOptions
        {
            TargetHost = host,
            EnabledSslProtocols = SslProtocols.Tls13,
            ClientCertificates = certificates,
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

/// <summary>
/// An HTTP/3 curl, used as a client that actually VALIDATES the server - which ioxide's own QUIC
/// client does not do, so nothing driven by it can answer "would a real client accept this".
/// </summary>
/// <remarks>
/// Optional by design: h3 support is not in a stock curl, so a test that needs one skips where it
/// is missing rather than failing. Set <c>IOXIDE_CURL_H3</c> to point at a build, otherwise
/// <c>curl-h3</c> and then <c>curl</c> are tried and each is asked whether it really has HTTP3.
/// </remarks>
public static class CurlH3
{
    private static readonly Lazy<string?> Found = new(Locate);

    /// <summary>The binary to run, or null when this machine has no HTTP/3 curl.</summary>
    public static string? Path => Found.Value;

    public static bool Available => Found.Value is not null;

    private static string? Locate()
    {
        string? configured = Environment.GetEnvironmentVariable("IOXIDE_CURL_H3");

        foreach (string candidate in configured is null
                     ? ["curl-h3", "curl"]
                     : new[] { configured, "curl-h3", "curl" })
        {
            if (HasHttp3(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool HasHttp3(string binary)
    {
        try
        {
            (int exit, string stdout, _) = Run(binary, ["--version"], timeoutMs: 5000);

            // The Features line is the only claim that counts - a curl can link ngtcp2 and still
            // be built without the protocol.
            return exit == 0 && stdout.Contains("HTTP3");
        }
        catch (Exception)
        {
            return false;   // not on PATH, not executable: simply not a curl we can use
        }
    }

    /// <summary>Runs curl and hands back what it said. Never throws on a non-zero exit - a refused
    /// connection IS the result some tests are asserting on.</summary>
    public static (int Exit, string Stdout, string Stderr) Run(string binary, string[] args, int timeoutMs = 15000)
    {
        var info = new System.Diagnostics.ProcessStartInfo
        {
            FileName = binary,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (string a in args)
        {
            info.ArgumentList.Add(a);
        }

        using var process = System.Diagnostics.Process.Start(info)
            ?? throw new IOException($"could not start {binary}");

        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();

        if (!process.WaitForExit(timeoutMs))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{binary} did not exit within {timeoutMs}ms");
        }

        return (process.ExitCode, stdout, stderr);
    }

    /// <summary>
    /// One verified HTTP/3 GET. <paramref name="host"/> is what curl asks for by name - sent as SNI
    /// AND checked against the certificate it gets back - while the connection still goes to
    /// loopback, which is what --resolve is for.
    /// </summary>
    public static (int Exit, string Stdout, string Stderr) Get(string host, int port, string caPath, string path = "/")
        => Run(Path!,
        [
            "--http3-only",
            "--cacert", caPath,
            "--resolve", $"{host}:{port}:127.0.0.1",
            "--max-time", "15",
            "--silent", "--show-error",
            $"https://{host}:{port}{path}",
        ]);
}

/// <summary>
/// The system curl, over TCP - a client that validates the chain AND matches the name, which is
/// what makes it worth driving alongside SslStream.
/// </summary>
/// <remarks>
/// SslStream in this harness accepts any certificate, so it can say WHICH one arrived but never
/// whether anyone would take it. curl answers the second question, and it is the one that matters
/// while certificates are being replaced underneath it.
/// </remarks>
public static class Curl
{
    private static readonly Lazy<bool> Present = new(() =>
    {
        try
        {
            return CurlH3.Run("curl", ["--version"], timeoutMs: 5000).Exit == 0;
        }
        catch (Exception)
        {
            return false;
        }
    });

    public static bool Available => Present.Value;

    /// <summary>
    /// One verified HTTPS GET. <paramref name="host"/> is sent as SNI and checked against the
    /// certificate; the connection still goes to loopback, which is what --resolve is for.
    /// Optionally presents a client certificate, for a server that asks for one.
    /// </summary>
    public static (int Exit, string Stdout, string Stderr) Get(string host, int port, string caPath,
        string path = "/", string? clientCert = null, string? clientKey = null, int timeoutMs = 15000)
    {
        var args = new List<string>
        {
            "--cacert", caPath,
            "--resolve", $"{host}:{port}:127.0.0.1",
            "--max-time", "15",
            "--silent", "--show-error",
        };

        if (clientCert is not null && clientKey is not null)
        {
            args.Add("--cert");
            args.Add(clientCert);
            args.Add("--key");
            args.Add(clientKey);
        }

        args.Add($"https://{host}:{port}{path}");

        return CurlH3.Run("curl", [.. args], timeoutMs);
    }
}
