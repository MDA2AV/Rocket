using System.Text;
using ioxide;
using ioxide.tls;

namespace Ioxide.Tests;

/// <summary>
/// Mutual TLS on the TCP side: the server asking the CLIENT for a certificate.
///
/// Driven with SslStream as the client, so a passing test means ioxide agrees with an independent
/// implementation rather than only with itself. The handler reports what it sees, which is the
/// point - enforcing an identity is only half of it if nothing can act on one.
///
/// Every case runs twice where the kernel module allows it: once on the OpenSSL path and once with
/// KernelTx. mTLS is settled during the handshake, which OpenSSL performs either way, so the two
/// must agree - and asserting that is cheaper than reasoning about it.
/// </summary>
internal static class MutualTlsTests
{
    public static void Register(Runner runner, bool ktls)
    {
        foreach ((string label, bool kernelTx) in Paths(ktls))
        {
            runner.Test($"mtls{label}: a client certificate is verified and its subject reaches the handler", () =>
            {
                (string ca, string serverCert, string serverKey, string clientCert, string clientKey, _, _)
                    = TestCert.EnsureMutualTls();

                int port = TestServer.Start(IdentityHandler, r => TlsService.Start(r, new TlsOptions
                {
                    CertificatePath = serverCert,
                    KeyPath = serverKey,
                    ClientCaPath = ca,
                    RequireClientCertificate = true,
                    KernelTx = kernelTx,
                }));

                (int status, string body) = Client.GetTlsClientCert(port, "/who", clientCert, clientKey);

                Assert.Equal(200, status);
                Assert.True(body.Contains("alice"), $"the handler should have seen CN=alice, got: {body}");
            });

            runner.Test($"mtls{label}: a client with no certificate is refused when one is required", () =>
            {
                (string ca, string serverCert, string serverKey, _, _, _, _) = TestCert.EnsureMutualTls();

                int port = TestServer.Start(IdentityHandler, r => TlsService.Start(r, new TlsOptions
                {
                    CertificatePath = serverCert,
                    KeyPath = serverKey,
                    ClientCaPath = ca,
                    RequireClientCertificate = true,
                    KernelTx = kernelTx,
                }));

                Assert.True(HandshakeFails(port, null, null),
                    "a client presenting no certificate should have been refused");
            });

            runner.Test($"mtls{label}: a certificate from another CA is refused", () =>
            {
                (string ca, string serverCert, string serverKey, _, _, string rogueCert, string rogueKey)
                    = TestCert.EnsureMutualTls();

                int port = TestServer.Start(IdentityHandler, r => TlsService.Start(r, new TlsOptions
                {
                    CertificatePath = serverCert,
                    KeyPath = serverKey,
                    ClientCaPath = ca,
                    RequireClientCertificate = true,
                    KernelTx = kernelTx,
                }));

                // Well-formed and correctly signed - by a CA this server does not trust. Holding a
                // certificate is not the same as holding one that means anything here.
                Assert.True(HandshakeFails(port, rogueCert, rogueKey),
                    "a certificate from an untrusted CA should have been refused");
            });
        }

        runner.Test("mtls: without RequireClientCertificate an anonymous client connects, unauthenticated", () =>
        {
            (string ca, string serverCert, string serverKey, _, _, _, _) = TestCert.EnsureMutualTls();

            // The mixed port: anyone may connect, and the handler decides what an unauthenticated
            // one is allowed to reach.
            int port = TestServer.Start(IdentityHandler, r => TlsService.Start(r, new TlsOptions
            {
                CertificatePath = serverCert,
                KeyPath = serverKey,
                ClientCaPath = ca,
                RequireClientCertificate = false,
            }));

            (int status, string body) = Client.GetTlsClientCert(port, "/who", null, null);

            Assert.Equal(200, status);
            Assert.Equal("anonymous", body);
        });

        runner.Test("mtls: a certificate still verifies when one is optional", () =>
        {
            (string ca, string serverCert, string serverKey, string clientCert, string clientKey, _, _)
                = TestCert.EnsureMutualTls();

            int port = TestServer.Start(IdentityHandler, r => TlsService.Start(r, new TlsOptions
            {
                CertificatePath = serverCert,
                KeyPath = serverKey,
                ClientCaPath = ca,
                RequireClientCertificate = false,
            }));

            (int status, string body) = Client.GetTlsClientCert(port, "/who", clientCert, clientKey);

            Assert.Equal(200, status);
            Assert.True(body.Contains("alice"), $"an optional certificate must still be read, got: {body}");
        });

        runner.Test("mtls: a rogue certificate is refused even when one is only optional", () =>
        {
            (string ca, string serverCert, string serverKey, _, _, string rogueCert, string rogueKey)
                = TestCert.EnsureMutualTls();

            // "Optional" governs presenting NOTHING. A certificate that is offered is always
            // verified - otherwise the option would read as "trust anything".
            int port = TestServer.Start(IdentityHandler, r => TlsService.Start(r, new TlsOptions
            {
                CertificatePath = serverCert,
                KeyPath = serverKey,
                ClientCaPath = ca,
                RequireClientCertificate = false,
            }));

            Assert.True(HandshakeFails(port, rogueCert, rogueKey),
                "an offered certificate must be verified whether or not one was required");
        });

        runner.Test("mtls: anchors from memory behave exactly as anchors from a file", () =>
        {
            (string ca, string serverCert, string serverKey, string clientCert, string clientKey, _, _)
                = TestCert.EnsureMutualTls();

            int port = TestServer.Start(IdentityHandler, r => TlsService.Start(r, new TlsOptions
            {
                CertificatePath = serverCert,
                KeyPath = serverKey,
                ClientCaPem = File.ReadAllText(ca),
                RequireClientCertificate = true,
            }));

            (int status, string body) = Client.GetTlsClientCert(port, "/who", clientCert, clientKey);

            Assert.Equal(200, status);
            Assert.True(body.Contains("alice"), $"the in-memory anchor should verify the same client, got: {body}");
        });

        runner.Test("mtls: no client CA leaves the handshake exactly as it was", () =>
        {
            (_, _, _, string clientCert, string clientKey, _, _) = TestCert.EnsureMutualTls();
            (string certPath, string keyPath) = TestCert.Ensure();

            // No anchors configured: nothing is requested, so a client that HAS a certificate is
            // never asked for it and connects anonymously.
            int port = TestServer.Start(IdentityHandler, r => TlsService.Start(r, new TlsOptions
            {
                CertificatePath = certPath,
                KeyPath = keyPath,
            }));

            (int status, string body) = Client.GetTlsClientCert(port, "/who", clientCert, clientKey);

            Assert.Equal(200, status);
            Assert.Equal("anonymous", body);
        });

        RegisterBadClientCertificates(runner);
        RegisterConfigurationErrors(runner);
    }

    // Configuration mistakes that should fail where they are written, not at some later handshake.
    /// <summary>
    /// Certificates that are legitimately issued by the trusted CA and still must not get in. Every
    /// other fixture in this suite is a clean, in-date, correctly-purposed leaf, so nothing here
    /// pinned the checks OpenSSL performs beyond the signature - a server that verified the chain
    /// and ignored everything else passed the whole suite.
    /// </summary>
    private static void RegisterBadClientCertificates(Runner runner)
    {
        (string Label, TestCert.ClientCertSpec Spec, string Why)[] rejects =
        [
            ("an expired client certificate", new TestCert.ClientCertSpec
            {
                Subject = "CN=expired-alice",
                // Inside the CA's own window: .NET refuses to issue a leaf that predates its
                // issuer, and the CA here starts a day ago.
                NotBefore = TimeSpan.FromHours(-23),
                NotAfter = TimeSpan.FromHours(-1),
            }, "validity is checked, not just the signature"),

            ("a client certificate that is not valid yet", new TestCert.ClientCertSpec
            {
                Subject = "CN=future-alice",
                NotBefore = TimeSpan.FromDays(1),
                NotAfter = TimeSpan.FromDays(30),
            }, "notBefore is checked as well as notAfter"),

            ("a client certificate for the wrong purpose", new TestCert.ClientCertSpec
            {
                Subject = "CN=server-alice",
                ExtendedKeyUsage = "1.3.6.1.5.5.7.3.1",   // serverAuth only
            }, "the extended key usage decides what a certificate may be USED for"),
        ];

        foreach ((string label, TestCert.ClientCertSpec spec, string why) in rejects)
        {
            runner.Test($"mtls: {label} is refused", () =>
            {
                (string ca, string cert, string key) = TestCert.EnsureClientCert(spec);
                (string serverCert, string serverKey) = ServerFor(ca);

                int port = TestServer.Start(IdentityHandler, r => TlsService.Start(r, new TlsOptions
                {
                    CertificatePath = serverCert,
                    KeyPath = serverKey,
                    ClientCaPath = ca,
                    RequireClientCertificate = true,
                }));

                Client.TlsOutcome outcome = Client.TryGetTls(port, "/who", cert, key);
                Assert.True(outcome != Client.TlsOutcome.Served, $"{label} was SERVED - {why}");
                Assert.True(outcome != Client.TlsOutcome.TimedOut, $"{label} made the server hang rather than refuse it");
            });
        }

        runner.Test("mtls: a client certificate that chains through an intermediate is accepted", () =>
        {
            // Every other fixture is signed DIRECTLY by the anchor, so a server that never built a
            // chain at all would pass all of them. Here the anchor is the root and the client sends
            // leaf + intermediate, which is what any real internal PKI looks like.
            (string anchors, string cert, string key) = TestCert.EnsureChainedClientCert();
            (string serverCert, string serverKey) = ServerFor(anchors);

            int port = TestServer.Start(CommonNameHandler, r => TlsService.Start(r, new TlsOptions
            {
                CertificatePath = serverCert,
                KeyPath = serverKey,
                ClientCaPath = anchors,   // root AND intermediate
                RequireClientCertificate = true,
            }));

            (int status, string body) = Client.GetTlsClientCert(port, "/who", cert, key);
            Assert.Equal(200, status);
            Assert.True(body.Contains("chained-alice"), $"expected the leaf's CN to reach the handler, got: {body}");
        });

        runner.Test("mtls: an EC client certificate is accepted", () =>
        {
            // Every fixture in this suite is RSA, so nothing here would notice a server that only
            // worked with RSA client keys.
            (string ca, string cert, string key) = TestCert.EnsureClientCert(new TestCert.ClientCertSpec
            {
                Subject = "CN=ec-alice",
                EllipticCurve = true,
            });
            (string serverCert, string serverKey) = ServerFor(ca);

            int port = TestServer.Start(CommonNameHandler, r => TlsService.Start(r, new TlsOptions
            {
                CertificatePath = serverCert,
                KeyPath = serverKey,
                ClientCaPath = ca,
                RequireClientCertificate = true,
            }));

            (int status, string body) = Client.GetTlsClientCert(port, "/who", cert, key);
            Assert.Equal(200, status);
            Assert.True(body.Contains("ec-alice"), $"expected CN=ec-alice, got: {body}");
        });
    }

    /// <summary>The server half of the mutual-TLS fixture, for tests that only vary the client.</summary>
    private static (string CertPath, string KeyPath) ServerFor(string ca)
    {
        (_, string serverCert, string serverKey, _, _, _, _) = TestCert.EnsureMutualTls();
        return (serverCert, serverKey);
    }

    private static void RegisterConfigurationErrors(Runner runner)
    {
        runner.Test("mtls: a trust bundle with a corrupt block is refused, not silently truncated", () =>
        {
            // ClientCaPath and ClientCaPem are documented as equivalent in what they trust. They
            // were not: the file route refuses such a bundle outright, while the PEM-text route
            // read until it stopped and kept whatever came BEFORE the bad block - so every client
            // issued by a later anchor was refused, with nothing said server-side. Failing closed
            // is not the same as failing loudly, and an operator has no way to see the difference.
            (string ca, _, _, _, _, _, _) = TestCert.EnsureMutualTls();

            Assert.True(StartFails(new TlsOptions
            {
                CertificatePath = TestCert.Ensure().CertPath,
                KeyPath = TestCert.Ensure().KeyPath,
                ClientCaPem = BundleWithCorruptMiddle(ca),
                RequireClientCertificate = true,
            }, "malformed"), "a bundle with a corrupt block should be refused at startup");
        });

        runner.Test("mtls: requiring a certificate without anchors is refused", () =>
        {
            (string certPath, string keyPath) = TestCert.Ensure();

            // Nothing to validate against: this would refuse every client sending none and accept
            // anything from one that sends something - the opposite of what it reads as.
            Assert.True(StartFails(new TlsOptions
            {
                CertificatePath = certPath,
                KeyPath = keyPath,
                RequireClientCertificate = true,
            }, "RequireClientCertificate needs trust anchors"),
                "RequireClientCertificate without anchors must be rejected");
        });

        runner.Test("mtls: two client CA sources are refused", () =>
        {
            (string ca, string serverCert, string serverKey, _, _, _, _) = TestCert.EnsureMutualTls();

            Assert.True(StartFails(new TlsOptions
            {
                CertificatePath = serverCert,
                KeyPath = serverKey,
                ClientCaPath = ca,
                ClientCaPem = File.ReadAllText(ca),
            }, "At most one client CA source"),
                "ClientCaPath and ClientCaPem together must be rejected");
        });

        runner.Test("mtls: the common name is reported exactly, not as part of a rendered subject", () =>
        {
            (string ca, string serverCert, string serverKey, string clientCert, string clientKey, _, _)
                = TestCert.EnsureMutualTls();

            int port = TestServer.Start(CommonNameHandler, r => TlsService.Start(r, new TlsOptions
            {
                CertificatePath = serverCert,
                KeyPath = serverKey,
                ClientCaPath = ca,
                RequireClientCertificate = true,
            }));

            (int status, string body) = Client.GetTlsClientCert(port, "/who", clientCert, clientKey);

            Assert.Equal(200, status);
            // Equality, not Contains: the value is meant to be compared whole.
            Assert.Equal("alice", body);
        });

        runner.Test("mtls: a subject crafted to look like another identity does not become one", () =>
        {
            // The reason PeerCommonName exists. The rendered subject escapes a literal '/' as
            // "\\/", which still CONTAINS '/', so this legitimately-issued certificate renders as
            //     /O=Acme\\/CN=admin.internal/CN=mallory
            // and satisfies Contains("/CN=admin.internal") while being an entirely different
            // principal. The structural CN is unmoved by any of that.
            (_, string serverCert, string serverKey, _, _, _, _) = TestCert.EnsureMutualTls();
            (string ca, string certPath, string keyPath) =
                TestCert.EnsureClientCertFromCa("O=Acme/CN=admin.internal, CN=mallory");

            int port = TestServer.Start(CommonNameHandler, r => TlsService.Start(r, new TlsOptions
            {
                CertificatePath = serverCert,
                KeyPath = serverKey,
                ClientCaPath = ca,
                RequireClientCertificate = true,
            }));

            (int status, string body) = Client.GetTlsClientCert(port, "/who", certPath, keyPath);

            Assert.Equal(200, status);
            Assert.Equal("mallory", body);
        });

        runner.Test("mtls: the rendered subject is the one that IS confusable", () =>
        {
            // The other half of the pair, asserted so the trap is a recorded fact rather than a
            // remark in a doc comment. If a future change makes the rendered form unambiguous this
            // test fails, and that is the moment to revisit what the docs promise.
            (_, string serverCert, string serverKey, _, _, _, _) = TestCert.EnsureMutualTls();
            (string ca, string certPath, string keyPath) =
                TestCert.EnsureClientCertFromCa("O=Acme/CN=admin.internal, CN=mallory");

            int port = TestServer.Start(IdentityHandler, r => TlsService.Start(r, new TlsOptions
            {
                CertificatePath = serverCert,
                KeyPath = serverKey,
                ClientCaPath = ca,
                RequireClientCertificate = true,
            }));

            (int status, string body) = Client.GetTlsClientCert(port, "/who", certPath, keyPath);

            Assert.Equal(200, status);
            Assert.True(body.Contains("/CN=admin.internal"),
                $"expected the rendered subject to be substring-confusable, got: {body}");
        });

        runner.Test("mtls: an unreadable client CA fails at startup", () =>
        {
            (string certPath, string keyPath) = TestCert.Ensure();

            Assert.True(StartFails(new TlsOptions
            {
                CertificatePath = certPath,
                KeyPath = keyPath,
                ClientCaPath = "/nonexistent/ca.pem",
            }, "/nonexistent/ca.pem"),
                "a missing CA file must be reported at startup");
        });
    }

    /// <summary>
    /// A trust bundle whose MIDDLE block is corrupt: a real anchor, then a well-formed PEM envelope
    /// around bytes that are not a certificate, then a second real anchor. The shape a secrets
    /// store or a partial write produces, and the one where "read until the reader stops" quietly
    /// trusts a narrower set than the operator wrote.
    /// </summary>
    private static string BundleWithCorruptMiddle(string caPath)
    {
        string anchor = File.ReadAllText(caPath).Trim();
        return anchor + "\n"
            + "-----BEGIN CERTIFICATE-----\n"
            + "bm90IGEgY2VydGlmaWNhdGU=\n"
            + "-----END CERTIFICATE-----\n"
            + anchor + "\n";
    }

    private static (string Label, bool KernelTx)[] Paths(bool ktls) =>
        ktls ? [(" (openssl)", false), (" (ktls)", true)] : [(" (openssl)", false)];

    /// <summary>
    /// Whether the handshake was refused. Which exception arrives depends on which side noticed
    /// first, so the assertion is on refusal rather than on its spelling.
    /// </summary>
    private static bool HandshakeFails(int port, string? certPath, string? keyPath)
    {
        try
        {
            Client.GetTlsClientCert(port, "/who", certPath, keyPath);
            return false;
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>
    /// Whether starting with these options was refused FOR THE STATED REASON. The reason matters:
    /// without it this also passes when the port was already bound or the reactor died on the way
    /// up, and reports that as the configuration refusal it was looking for.
    /// </summary>
    private static bool StartFails(TlsOptions options, string because)
    {
        try
        {
            TestServer.Start(EmptyHandler, r => TlsService.Start(r, options));
            return false;
        }
        catch (Exception e)
        {
            return e.Message.Contains(because);
        }
    }

    // Answers with the authenticated identity, or "anonymous". Reading it is the whole point of
    // the feature: a server that can only enforce cannot authorise.
    private static Task IdentityHandler(Reactor reactor, TcpConnection connection)
        => Identity(reactor, connection, static s => s.PeerSubject);

    // The same, reporting the structural CN - the value an application is meant to authorize on.
    private static Task CommonNameHandler(Reactor reactor, TcpConnection connection)
        => Identity(reactor, connection, static s => s.PeerCommonName);

    private static async Task Identity(Reactor reactor, TcpConnection connection,
        Func<TlsSession, string?> report)
    {
        TlsSession? session = null;
        try
        {
            session = await reactor.GetService<TlsService>()!.AcceptAsync(connection);

            while (true)
            {
                RecvSnapshot snapshot = await connection.ReadAsync();
                if (snapshot.IsClosed)
                {
                    return;
                }

                string subject = report(session) ?? "anonymous";
                byte[] body = Encoding.ASCII.GetBytes(subject);

                session.Write(connection, Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\ncontent-length: {body.Length}\r\n\r\n{subject}"));

                await connection.FlushAsync();
                connection.ResetRead();
            }
        }
        finally
        {
            session?.Dispose();
            connection.DecRef();
        }
    }

    private static Task EmptyHandler(Reactor reactor, TcpConnection connection)
    {
        connection.DecRef();
        return Task.CompletedTask;
    }
}
