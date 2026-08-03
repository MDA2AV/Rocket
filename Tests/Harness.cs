using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using ioxide;

namespace Ioxide.E2E;

/// <summary>
/// Starts an ioxide server on a unique loopback port and waits for it to listen. Most test servers
/// run on a background thread until the process exits; teardown-focused tests use the returned
/// reactor + thread to Stop() and join. Tiny buffers keep many concurrent test servers cheap.
/// </summary>
internal static class TestServer
{
    private static int _nextPort = 18080;

    /// <summary>Reserve a unique port (e.g. for TcpOptions.ExtraPorts).</summary>
    public static int NextPort() => Interlocked.Increment(ref _nextPort);

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
        config = config with { Tcp = config.Tcp with { Port = (ushort)port }, ReactorCount = 1 };

        var reactor = new Reactor(0, config)
        {
            OnStart = onStart,
            TcpHandle = handle,
        };

        var thread = new Thread(reactor.Run)
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

        var thread = new Thread(reactor.Run)
        {
            IsBackground = true,
            Name = $"test-reactor-udp-{udpPort}",
        };
        thread.Start();

        WaitForListen(tcpPort);
        return (tcpPort, udpPort);
    }

    /// <summary>
    /// A server with a TCP handler AND a QUIC socket configured for CLIENT use (no accept
    /// factory): the shape an app needs when its handlers make outbound HTTP/3 calls, since client
    /// connections ride the reactor's QUIC socket and their replies route back through it.
    /// </summary>
    public static int StartQuicClientHost(Func<Reactor, TcpConnection, Task> tcpHandle, Action<Reactor> onStart)
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
                ConnectionFactory = null,   // outbound only: nothing is accepted here
            },
        };

        var reactor = new Reactor(0, config)
        {
            TcpHandle = tcpHandle,
            OnStart = onStart,
        };

        var thread = new Thread(reactor.Run)
        {
            IsBackground = true,
            Name = $"test-reactor-h3client-{tcpPort}",
        };
        thread.Start();

        WaitForListen(tcpPort);
        return tcpPort;
    }

    private static void WaitForListen(int port)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
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
internal static class Client
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
internal static class Sidecars
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
internal static class TestCert
{
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
            using X509Certificate2 cert = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

            File.WriteAllText(certPath, cert.ExportCertificatePem());
            File.WriteAllText(keyPath, rsa.ExportPkcs8PrivateKeyPem());
        }

        return (certPath, keyPath);
    }
}
