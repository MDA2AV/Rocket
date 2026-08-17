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

        RegisterConfigurationErrors(runner);
    }

    // Configuration mistakes that should fail where they are written, not at some later handshake.
    private static void RegisterConfigurationErrors(Runner runner)
    {
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

    // Answers with the authenticated identity, or "anonymous". Reading PeerSubject is the whole
    // point of the feature: a server that can only enforce cannot authorise.
    private static async Task IdentityHandler(Reactor reactor, TcpConnection connection)
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

                string subject = session.PeerSubject is { } peer ? peer : "anonymous";
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
