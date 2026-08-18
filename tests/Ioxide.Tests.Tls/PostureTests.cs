using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using ioxide;
using ioxide.tls;

namespace Ioxide.Tests;

/// <summary>
/// The settings a deployment states rather than discovers: which TLS versions and ciphersuites are
/// on offer, and how long an unfinished handshake is tolerated.
///
/// Driven with SslStream as the client, because the assertion worth making is that a peer with a
/// different posture is actually turned away - not that ioxide called the right setter.
/// </summary>
internal static class PostureTests
{
    public static void Register(Runner runner)
    {
        runner.Test("posture: MinProtocolVersion Tls13 refuses a TLS 1.2 client", () =>
        {
            (string certPath, string keyPath) = TestCert.Ensure();

            int port = TestServer.Start(Handlers.Tls, r => TlsService.Start(r, new TlsOptions
            {
                CertificatePath = certPath,
                KeyPath = keyPath,
                MinProtocolVersion = TlsProtocolVersion.Tls13,
            }));

            Assert.True(!Handshakes(port, SslProtocols.Tls12),
                "a TLS 1.2 client should have been refused by a 1.3-only server");
            Assert.True(Handshakes(port, SslProtocols.Tls13),
                "a TLS 1.3 client should still be served");
        });

        runner.Test("posture: the default still serves a TLS 1.2 client", () =>
        {
            // The regression guard for everyone who sets nothing: the floor must not move on them.
            (string certPath, string keyPath) = TestCert.Ensure();

            int port = TestServer.Start(Handlers.Tls, r => TlsService.Start(r, new TlsOptions
            {
                CertificatePath = certPath,
                KeyPath = keyPath,
            }));

            Assert.True(Handshakes(port, SslProtocols.Tls12), "the default floor should still accept TLS 1.2");
        });

        runner.Test("posture: a ciphersuite list the client cannot meet fails the handshake", () =>
        {
            (string certPath, string keyPath) = TestCert.Ensure();

            // Server offers only AES-256; the client is told to offer only ChaCha20. Both are
            // TLS 1.3 suites, so nothing but the list itself decides the outcome.
            int port = TestServer.Start(Handlers.Tls, r => TlsService.Start(r, new TlsOptions
            {
                CertificatePath = certPath,
                KeyPath = keyPath,
                MinProtocolVersion = TlsProtocolVersion.Tls13,
                CipherSuites = "TLS_AES_256_GCM_SHA384",
            }));

            Assert.True(Handshakes(port, SslProtocols.Tls13, CipherSuitesPolicy(TlsCipherSuite.TLS_AES_256_GCM_SHA384)),
                "a client offering the one suite the server allows should be served");
            Assert.True(!Handshakes(port, SslProtocols.Tls13, CipherSuitesPolicy(TlsCipherSuite.TLS_CHACHA20_POLY1305_SHA256)),
                "a client offering only a suite the server excluded should have been refused");
        });

        runner.Test("posture: a ciphersuite name OpenSSL does not know fails at startup", () =>
        {
            // Not at the first handshake, which is where an unvalidated list would surface.
            (string certPath, string keyPath) = TestCert.Ensure();

            Assert.True(StartFails(new TlsOptions
            {
                CertificatePath = certPath,
                KeyPath = keyPath,
                CipherSuites = "TLS_NOT_A_REAL_SUITE",
            }, "set_ciphersuites"), "an unknown ciphersuite should be refused at startup");
        });

        runner.Test("posture: a ciphersuite list that is merely MOSTLY right fails at startup", () =>
        {
            // The dangerous shape, because OpenSSL reports success for it. set_ciphersuites ignores
            // names it does not know as long as ONE in the list is valid, so a single typo silently
            // drops the suite the operator meant to pin and the server runs with a narrower list
            // than it was configured with. The all-unknown case above already failed; this one did
            // not, which is why "it started" was never evidence the list was applied.
            (string certPath, string keyPath) = TestCert.Ensure();

            Assert.True(StartFails(new TlsOptions
            {
                CertificatePath = certPath,
                KeyPath = keyPath,
                CipherSuites = "TLS_AES_256_GCM_SHA384:TLS_CHACHA20_POLY1305_SHA25",   // one digit short
            }, "TLS_CHACHA20_POLY1305_SHA25"), "a typo among valid suites should be refused, and named");
        });

        runner.Test("posture: an empty ciphersuite list is refused rather than disabling TLS 1.3", () =>
        {
            // OpenSSL accepts "" and returns success, leaving NO TLS 1.3 suite enabled. Paired with
            // a 1.3 floor that is a server which starts perfectly and cannot complete a single
            // handshake; on the default floor it quietly becomes TLS 1.2-only. An empty string is
            // what a config binder produces from a blank field, so this is reachable by accident.
            (string certPath, string keyPath) = TestCert.Ensure();

            Assert.True(StartFails(new TlsOptions
            {
                CertificatePath = certPath,
                KeyPath = keyPath,
                CipherSuites = "",
            }, "empty"), "an empty ciphersuite list should be refused at startup");
        });

        runner.Test("posture: kTLS pinning a floor of 1.3 refuses a stated floor of 1.2", () =>
        {
            (string certPath, string keyPath) = TestCert.Ensure();

            Assert.True(StartFails(new TlsOptions
            {
                CertificatePath = certPath,
                KeyPath = keyPath,
                KernelTx = true,
                MinProtocolVersion = TlsProtocolVersion.Tls12,
            }, "cannot hold"), "two incompatible version demands should be named, not resolved by ordering");
        });

        runner.Test("posture: kTLS pinning one suite refuses a suite list of its own", () =>
        {
            (string certPath, string keyPath) = TestCert.Ensure();

            Assert.True(StartFails(new TlsOptions
            {
                CertificatePath = certPath,
                KeyPath = keyPath,
                KernelTx = true,
                CipherSuites = "TLS_AES_256_GCM_SHA384",
            }, "TLS_AES_128_GCM_SHA256"), "kTLS needs its one suite, and saying otherwise should be refused");
        });

        runner.Test("handshake timeout: a peer that connects and says nothing is dropped", () =>
        {
            // The point of the setting. Without it this socket is held for as long as the peer
            // likes, having authenticated nothing, at the cost of one connect().
            (string certPath, string keyPath) = TestCert.Ensure();

            int port = TestServer.Start(Handlers.Tls, r => TlsService.Start(r, new TlsOptions
            {
                CertificatePath = certPath,
                KeyPath = keyPath,
                HandshakeTimeoutMs = 1_000,
            }));

            using var client = new TcpClient();
            client.Connect("127.0.0.1", port);
            client.ReceiveTimeout = 8_000;

            // Not one byte of ClientHello. The server should close on its own.
            var buf = new byte[16];
            long start = Environment.TickCount64;
            int read;
            try
            {
                read = client.GetStream().Read(buf, 0, buf.Length);
            }
            catch (IOException)
            {
                read = 0;   // reset instead of orderly close - still the server giving up
            }
            long elapsed = Environment.TickCount64 - start;

            Assert.Equal(0, read);
            Assert.True(elapsed < 6_000, $"the server took {elapsed} ms to give up on a silent peer");
        });

        runner.Test("handshake timeout: a handshake that completes is not swept afterwards", () =>
        {
            // The sweep must not outlive the handshake it was watching: an established connection
            // idling past the timeout is an ordinary keep-alive, not a stalled handshake.
            (string certPath, string keyPath) = TestCert.Ensure();

            int port = TestServer.Start(Handlers.Tls, r => TlsService.Start(r, new TlsOptions
            {
                CertificatePath = certPath,
                KeyPath = keyPath,
                HandshakeTimeoutMs = 1_000,
            }));

            using var sock = new TcpClient();
            sock.Connect("127.0.0.1", port);
            using var ssl = new SslStream(sock.GetStream(), false, (_, _, _, _) => true);
            ssl.AuthenticateAsClient("localhost");

            // Well past the handshake deadline, then use the connection.
            Thread.Sleep(2_500);

            ssl.Write("GET / HTTP/1.1\r\nhost: localhost\r\n\r\n"u8.ToArray());
            ssl.Flush();

            var buf = new byte[256];
            sock.ReceiveTimeout = 5_000;
            int n = ssl.Read(buf, 0, buf.Length);

            Assert.True(n > 0 && System.Text.Encoding.ASCII.GetString(buf, 0, n).Contains("200"),
                "an established connection was swept as if it were still handshaking");
        });

        runner.Test("handshake timeout: 0 disables the sweep", () =>
        {
            (string certPath, string keyPath) = TestCert.Ensure();

            int port = TestServer.Start(Handlers.Tls, r => TlsService.Start(r, new TlsOptions
            {
                CertificatePath = certPath,
                KeyPath = keyPath,
                HandshakeTimeoutMs = 0,
            }));

            using var client = new TcpClient();
            client.Connect("127.0.0.1", port);
            client.ReceiveTimeout = 2_000;

            var buf = new byte[16];
            bool timedOut = false;
            try
            {
                client.GetStream().Read(buf, 0, buf.Length);
            }
            catch (IOException)
            {
                timedOut = true;   // the CLIENT gave up, which is the point: the server did not
            }

            Assert.True(timedOut, "with the sweep off the server should have held the connection");
        });
    }

    private static SslClientAuthenticationOptions CipherSuitesPolicy(TlsCipherSuite suite)
        => new()
        {
            TargetHost = "localhost",
            CipherSuitesPolicy = new CipherSuitesPolicy([suite]),
        };

    /// <summary>
    /// Whether the handshake completed. A refusal returns false; a server that HANGS throws, so
    /// that the negative tests here cannot be satisfied by one.
    /// </summary>
    /// <remarks>
    /// The distinction matters because every posture test that asserts a client is turned away
    /// reads this helper's false. A bare try/catch returns false for a refusal, for a hang, for a
    /// crash and for a port held by something else - so "the server refused a TLS 1.2 client" was
    /// satisfied by a server that had stopped answering anyone at all, which is a worse outcome
    /// than the one being ruled out.
    /// </remarks>
    private static bool Handshakes(int port, SslProtocols protocols, SslClientAuthenticationOptions? auth = null)
    {
        using var sock = new TcpClient();
        sock.Connect("127.0.0.1", port);
        sock.ReceiveTimeout = 6_000;
        sock.SendTimeout = 6_000;

        using var ssl = new SslStream(sock.GetStream(), false, (_, _, _, _) => true);

        try
        {
            if (auth is not null)
            {
                auth.EnabledSslProtocols = protocols;
                ssl.AuthenticateAsClient(auth);
            }
            else
            {
                ssl.AuthenticateAsClient("localhost", null, protocols, false);
            }

            return true;
        }
        catch (AuthenticationException)
        {
            return false;   // refused, which is what these tests mean by "does not handshake"
        }
        catch (IOException e) when (Timeout(e))
        {
            throw new Exception(
                $"the server on :{port} neither completed nor refused the handshake within 6 s - "
                + "a hang is not a refusal, and this assertion would have read it as one.", e);
        }
        catch (IOException)
        {
            return false;   // closed without an alert: rude, but still declined
        }

        static bool Timeout(Exception e)
        {
            for (Exception? at = e; at is not null; at = at.InnerException)
            {
                if (at is SocketException { SocketErrorCode: SocketError.TimedOut })
                {
                    return true;
                }
            }
            return false;
        }
    }

    private static bool StartFails(TlsOptions options, string because)
    {
        try
        {
            TestServer.Start(Handlers.Tls, r => TlsService.Start(r, options));
            return false;
        }
        catch (Exception e)
        {
            return e.Message.Contains(because);
        }
    }
}
