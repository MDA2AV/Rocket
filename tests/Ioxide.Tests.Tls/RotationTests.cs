using ioxide;
using ioxide.tls;

namespace Ioxide.Tests;

/// <summary>
/// Replacing certificates on a running server - what a renewal does, since an ACME client rewrites
/// its PEM every couple of months and restarting to pick it up is an outage.
/// </summary>
/// <remarks>
/// Every test asserts on the SUBJECT the server answered with, before and after, because that is
/// the whole observable effect. A service that ignored the call would keep passing the "before"
/// half and fail the "after" one.
/// </remarks>
internal static class RotationTests
{
    public static void Register(Runner runner, bool ktls)
    {
        foreach ((string label, bool kernelTx) in Paths(ktls))
        {
            runner.Test($"rotate{label}: the default certificate is replaced for new connections", () =>
            {
                (string first, string firstKey) = TestCert.EnsureNamed("first.test");
                (string second, string secondKey) = TestCert.EnsureNamed("second.test");

                TlsService? service = null;
                int port = TestServer.Start(OkHandler, r => service = TlsService.Start(r, new TlsOptions
                {
                    CertificatePath = first,
                    KeyPath = firstKey,
                    KernelTx = kernelTx,
                }));

                Assert.True(Client.ServerCertificateSubject(port, null).Contains("first.test"),
                    "the server should start on the certificate it was given");

                service!.ReplaceCertificates(new TlsCertificate { CertificatePath = second, KeyPath = secondKey });

                Assert.True(Client.ServerCertificateSubject(port, null).Contains("second.test"),
                    "a connection made after the rotation should be answered with the new certificate");
            });

            runner.Test($"rotate{label}: a rotated connection still serves requests", () =>
            {
                // The certificate is only half of it. A rotation that produced a context missing
                // something the connection needs afterwards - its keylog callback, which is what
                // the kTLS handoff reads the traffic secret from - would hand back a perfect
                // certificate on a connection that then dies. Under the ktls label this is the
                // only test that drives kTLS across a rotation.
                (string cert, string key) = TestCert.Ensure();
                (string second, string secondKey) = TestCert.EnsureNamed("second.test");

                TlsService? service = null;
                int port = TestServer.Start(OkHandler, r => service = TlsService.Start(r, new TlsOptions
                {
                    CertificatePath = cert,
                    KeyPath = key,
                    KernelTx = kernelTx,
                }));

                service!.ReplaceCertificates(new TlsCertificate { CertificatePath = second, KeyPath = secondKey });

                (string subject, _, int status, string body) = Client.GetTlsSni(port, "/", null);

                Assert.True(subject.Contains("second.test"), $"wrong certificate after rotation: {subject}");
                Assert.Equal(200, status);
                Assert.True(body.Contains("ok"), $"a rotated connection should answer, got: {body}");
            });
        }

        runner.Test("rotate: the SNI table is replaced too", () =>
        {
            (string cert, string key) = TestCert.Ensure();
            (string alpha, string alphaKey) = TestCert.EnsureNamed("alpha.test");
            (string beta, string betaKey) = TestCert.EnsureNamed("beta.test");

            TlsService? service = null;
            int port = TestServer.Start(OkHandler, r => service = TlsService.Start(r, new TlsOptions
            {
                CertificatePath = cert,
                KeyPath = key,
                CertificatesByHost = new Dictionary<string, TlsCertificate>
                {
                    ["named.test"] = new() { CertificatePath = alpha, KeyPath = alphaKey },
                },
            }));

            Assert.True(Client.ServerCertificateSubject(port, "named.test").Contains("alpha.test"),
                "the name should start on the certificate it was configured with");

            // Same name, different certificate - which is what renewing one host looks like.
            service!.ReplaceCertificates(
                new TlsCertificate { CertificatePath = cert, KeyPath = key },
                new Dictionary<string, TlsCertificate>
                {
                    ["named.test"] = new() { CertificatePath = beta, KeyPath = betaKey },
                });

            Assert.True(Client.ServerCertificateSubject(port, "named.test").Contains("beta.test"),
                "the name should be answered with the certificate the rotation installed");
        });

        runner.Test("rotate: a name can be added to a running service", () =>
        {
            // A service that started with no table at all has no servername callback installed, so
            // this also covers the callback being wired by the rotation rather than at startup.
            (string cert, string key) = TestCert.Ensure();
            (string alpha, string alphaKey) = TestCert.EnsureNamed("alpha.test");

            TlsService? service = null;
            int port = TestServer.Start(OkHandler, r => service = TlsService.Start(r, new TlsOptions
            {
                CertificatePath = cert,
                KeyPath = key,
            }));

            Assert.True(Client.ServerCertificateSubject(port, "alpha.test").Contains("localhost"),
                "before the rotation the name is unknown and gets the default");

            service!.ReplaceCertificates(
                new TlsCertificate { CertificatePath = cert, KeyPath = key },
                new Dictionary<string, TlsCertificate>
                {
                    ["alpha.test"] = new() { CertificatePath = alpha, KeyPath = alphaKey },
                });

            Assert.True(Client.ServerCertificateSubject(port, "alpha.test").Contains("alpha.test"),
                "a name added by a rotation should be served");
            Assert.True(service.ServerNames.Contains("alpha.test"), "and reported");
        });

        runner.Test("rotate: a name can be dropped from a running service", () =>
        {
            (string cert, string key) = TestCert.Ensure();
            (string alpha, string alphaKey) = TestCert.EnsureNamed("alpha.test");

            TlsService? service = null;
            int port = TestServer.Start(OkHandler, r => service = TlsService.Start(r, new TlsOptions
            {
                CertificatePath = cert,
                KeyPath = key,
                CertificatesByHost = new Dictionary<string, TlsCertificate>
                {
                    ["alpha.test"] = new() { CertificatePath = alpha, KeyPath = alphaKey },
                },
            }));

            Assert.True(Client.ServerCertificateSubject(port, "alpha.test").Contains("alpha.test"),
                "the name starts served");

            service!.ReplaceCertificates(new TlsCertificate { CertificatePath = cert, KeyPath = key });

            Assert.True(Client.ServerCertificateSubject(port, "alpha.test").Contains("localhost"),
                "a name left out of a rotation should fall back to the default, not keep answering");
            Assert.True(service.ServerNames.Count == 0, "and should no longer be reported");
        });

        runner.Test("rotate: a rotation that cannot be built leaves the old certificates serving", () =>
        {
            // The property that makes this safe to automate: a renewal script pointing at a path
            // that is not there yet must not take the site down.
            (string cert, string key) = TestCert.Ensure();

            TlsService? service = null;
            int port = TestServer.Start(OkHandler, r => service = TlsService.Start(r, new TlsOptions
            {
                CertificatePath = cert,
                KeyPath = key,
            }));

            bool threw = false;

            try
            {
                service!.ReplaceCertificates(
                    new TlsCertificate { CertificatePath = "/nonexistent/new.crt", KeyPath = "/nonexistent/new.key" });
            }
            catch (IOException)
            {
                threw = true;
            }

            Assert.True(threw, "a rotation naming a certificate that cannot be read should throw");

            Assert.True(Client.ServerCertificateSubject(port, null).Contains("localhost"),
                "and the service should still be serving the certificate it had");
        });

        runner.Test("rotate: a rotation refusing a bad table leaves the old one serving", () =>
        {
            (string cert, string key) = TestCert.Ensure();
            (string alpha, string alphaKey) = TestCert.EnsureNamed("alpha.test");

            TlsService? service = null;
            int port = TestServer.Start(OkHandler, r => service = TlsService.Start(r, new TlsOptions
            {
                CertificatePath = cert,
                KeyPath = key,
                CertificatesByHost = new Dictionary<string, TlsCertificate>
                {
                    ["alpha.test"] = new() { CertificatePath = alpha, KeyPath = alphaKey },
                },
            }));

            bool threw = false;

            try
            {
                // Two spellings of one name: refused when a service starts, and refused here too.
                service!.ReplaceCertificates(
                    new TlsCertificate { CertificatePath = cert, KeyPath = key },
                    new Dictionary<string, TlsCertificate>
                    {
                        ["dup.test"] = new() { CertificatePath = alpha, KeyPath = alphaKey },
                        ["DUP.test"] = new() { CertificatePath = cert, KeyPath = key },
                    });
            }
            catch (ArgumentException)
            {
                threw = true;
            }

            Assert.True(threw, "a rotation naming one host twice should be refused");

            Assert.True(Client.ServerCertificateSubject(port, "alpha.test").Contains("alpha.test"),
                "and the table it had should be untouched");
        });

        runner.Test("rotate: renewing a certificate does not drop mutual TLS", () =>
        {
            // Every context in a new generation is rebuilt, client verification included. If that
            // rebuild ever stopped carrying the anchors, renewing a certificate would quietly open
            // the port - so this asserts the demand and the validation both survive.
            (string ca, string serverCert, string serverKey, string clientCert, string clientKey,
             string rogueCert, string rogueKey) = TestCert.EnsureMutualTls();
            (string alpha, string alphaKey) = TestCert.EnsureNamed("alpha.test");
            (string beta, string betaKey) = TestCert.EnsureNamed("beta.test");

            TlsService? service = null;
            int port = TestServer.Start(OkHandler, r => service = TlsService.Start(r, new TlsOptions
            {
                CertificatePath = serverCert,
                KeyPath = serverKey,
                ClientCaPath = ca,
                RequireClientCertificate = true,
                CertificatesByHost = new Dictionary<string, TlsCertificate>
                {
                    ["alpha.test"] = new() { CertificatePath = alpha, KeyPath = alphaKey },
                },
            }));

            service!.ReplaceCertificates(
                new TlsCertificate { CertificatePath = serverCert, KeyPath = serverKey },
                new Dictionary<string, TlsCertificate>
                {
                    ["alpha.test"] = new() { CertificatePath = beta, KeyPath = betaKey },
                });

            Assert.True(HandshakeFails(port, null, null, "alpha.test"),
                "a client with no certificate must still be refused after a rotation");
            Assert.True(HandshakeFails(port, rogueCert, rogueKey, "alpha.test"),
                "a certificate from another CA must still be refused after a rotation");

            (int status, _) = Client.GetTlsClientCert(port, "/", clientCert, clientKey, host: "alpha.test");
            Assert.Equal(200, status);
        });

        runner.Test("rotate: rotations from several threads all take effect", () =>
        {
            // Concurrent renewals - two ACME hooks, or an admin endpoint fired twice. The managed
            // side has always taken a lock here; this is what would notice if it stopped.
            (string cert, string key) = TestCert.Ensure();
            (string alpha, string alphaKey) = TestCert.EnsureNamed("alpha.test");
            (string beta, string betaKey) = TestCert.EnsureNamed("beta.test");

            TlsService? service = null;
            int port = TestServer.Start(OkHandler, r => service = TlsService.Start(r, new TlsOptions
            {
                CertificatePath = cert,
                KeyPath = key,
                CertificatesByHost = new Dictionary<string, TlsCertificate>
                {
                    ["alpha.test"] = new() { CertificatePath = alpha, KeyPath = alphaKey },
                },
            }));

            Exception? failure = null;

            Parallel.For(0, 4, i =>
            {
                try
                {
                    for (int n = 0; n < 20; n++)
                    {
                        service!.ReplaceCertificates(
                            new TlsCertificate { CertificatePath = cert, KeyPath = key },
                            new Dictionary<string, TlsCertificate>
                            {
                                ["alpha.test"] = i % 2 == 0
                                    ? new() { CertificatePath = beta, KeyPath = betaKey }
                                    : new() { CertificatePath = alpha, KeyPath = alphaKey },
                            });
                    }
                }
                catch (Exception e)
                {
                    failure = e;
                }
            });

            Assert.True(failure is null, $"a concurrent rotation failed: {failure}");

            string subject = Client.ServerCertificateSubject(port, "alpha.test");
            Assert.True(subject.Contains("alpha.test") || subject.Contains("beta.test"),
                $"the service should still serve a certificate for the name, got: {subject}");
        });

        runner.Test("rotate: connections keep working across a rotation under load", () =>
        {
            // The reason any of this is careful: handshakes are running on the reactor while the
            // certificates are replaced. If a rotation freed what a handshake was about to use,
            // this is where it would crash rather than fail an assertion.
            (string cert, string key) = TestCert.Ensure();
            (string alpha, string alphaKey) = TestCert.EnsureNamed("alpha.test");
            (string beta, string betaKey) = TestCert.EnsureNamed("beta.test");

            TlsService? service = null;
            int port = TestServer.Start(OkHandler, r => service = TlsService.Start(r, new TlsOptions
            {
                CertificatePath = cert,
                KeyPath = key,
                CertificatesByHost = new Dictionary<string, TlsCertificate>
                {
                    ["alpha.test"] = new() { CertificatePath = alpha, KeyPath = alphaKey },
                },
            }));

            using var stop = new CancellationTokenSource();
            int completed = 0;
            Exception? failure = null;

            var load = Task.Run(() =>
            {
                try
                {
                    while (!stop.IsCancellationRequested)
                    {
                        (_, _, int status, _) = Client.GetTlsSni(port, "/", "alpha.test");
                        if (status == 200)
                        {
                            Interlocked.Increment(ref completed);
                        }
                    }
                }
                catch (Exception e)
                {
                    failure = e;
                }
            });

            // Rotate repeatedly underneath it, alternating which certificate the name answers with.
            for (int i = 0; i < 20; i++)
            {
                bool even = i % 2 == 0;

                service!.ReplaceCertificates(
                    new TlsCertificate { CertificatePath = cert, KeyPath = key },
                    new Dictionary<string, TlsCertificate>
                    {
                        ["alpha.test"] = even
                            ? new() { CertificatePath = beta, KeyPath = betaKey }
                            : new() { CertificatePath = alpha, KeyPath = alphaKey },
                    });

                Thread.Sleep(15);
            }

            stop.Cancel();
            load.Wait(TimeSpan.FromSeconds(10));

            Assert.True(failure is null, $"a handshake failed while certificates were being replaced: {failure}");
            Assert.True(completed > 0, "no request completed during the rotations");
        });
    }

    /// <summary>Whether the handshake was refused, presenting the given certificate for a name.</summary>
    private static bool HandshakeFails(int port, string? certPath, string? keyPath, string host)
    {
        try
        {
            Client.GetTlsClientCert(port, "/", certPath, keyPath, host: host);
            return false;
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>Completes the handshake and answers, so a certificate can be read AND used.</summary>
    private static async Task OkHandler(Reactor reactor, TcpConnection connection)
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

                session.Write(connection, "HTTP/1.1 200 OK\r\ncontent-length: 2\r\n\r\nok"u8.ToArray());

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

    private static IEnumerable<(string Label, bool KernelTx)> Paths(bool ktls)
    {
        yield return ("", false);

        if (ktls)
        {
            yield return (" (ktls)", true);
        }
    }
}
