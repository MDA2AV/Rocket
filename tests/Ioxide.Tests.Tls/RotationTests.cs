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
    private static readonly byte[] Response = "HTTP/1.1 200 OK\r\ncontent-length: 2\r\n\r\nok"u8.ToArray();

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

        runner.Test("rotate: trust anchors are re-read, so who may connect can be changed", () =>
        {
            // TlsOptions documents this as the way to revoke an issuer: ClientCaPath is re-read
            // whenever a context is built, and a rotation rebuilds every context. It is the only
            // documented mechanism for changing who is allowed in without restarting, and nothing
            // tested it - while the QUIC side, whose parameter doc claims the same behaviour, does
            // not actually do it.
            //
            // Driven with a chain, because that makes the anchor set the ONLY variable: the client
            // certificate, the server certificate and the CA all stay exactly the same, and the
            // only thing that changes is whether the file names the intermediate needed to build
            // the chain.
            (string anchorsWithIntermediate, string intermediate, string clientCert, string clientKey)
                = TestCert.EnsureChainedClientCert();
            (string ca, string serverCert, string serverKey, _, _, _, _) = TestCert.EnsureMutualTls();

            string anchors = Path.Combine(Path.GetTempPath(), $"ioxide-rotate-anchors-{Environment.ProcessId}.pem");
            File.WriteAllText(anchors, File.ReadAllText(ca));   // the root alone

            try
            {
                var options = new TlsOptions
                {
                    CertificatePath = serverCert,
                    KeyPath = serverKey,
                    ClientCaPath = anchors,
                    RequireClientCertificate = true,
                };

                TlsService? service = null;
                int port = TestServer.Start(Handlers.Tls, r => service = TlsService.Start(r, options));

                // The root alone cannot build this client's chain: the issuing intermediate is
                // neither anchored nor sent.
                Assert.True(Client.TryGetTls(port, "/", clientCert, clientKey) != Client.TlsOutcome.Served,
                    "a leaf whose issuer is not anchored should not be served");

                // Now name the intermediate as well and rotate. Nothing else changes.
                File.WriteAllText(anchors, File.ReadAllText(anchorsWithIntermediate));
                service!.ReplaceCertificates(new TlsCertificate
                {
                    CertificatePath = serverCert,
                    KeyPath = serverKey,
                });

                Assert.Equal(Client.TlsOutcome.Served, Client.TryGetTls(port, "/", clientCert, clientKey));
            }
            finally
            {
                File.Delete(anchors);
            }
        });

        runner.Test("rotate: an anchor file emptied under a rotation stops admitting anyone", () =>
        {
            // The direction that matters for revocation, and the one an operator would actually
            // perform. Same client, same server certificate; the anchor file loses the CA that
            // issued the client, and the rotation must make that take effect rather than keep
            // serving whoever was admissible when the process started.
            (string ca, string serverCert, string serverKey, string clientCert, string clientKey, _, _)
                = TestCert.EnsureMutualTls();
            (string unrelated, _) = TestCert.EnsureNamed("not-the-client-ca.test");

            string anchors = Path.Combine(Path.GetTempPath(), $"ioxide-revoke-anchors-{Environment.ProcessId}.pem");
            File.WriteAllText(anchors, File.ReadAllText(ca));

            try
            {
                var options = new TlsOptions
                {
                    CertificatePath = serverCert,
                    KeyPath = serverKey,
                    ClientCaPath = anchors,
                    RequireClientCertificate = true,
                };

                TlsService? service = null;
                int port = TestServer.Start(Handlers.Tls, r => service = TlsService.Start(r, options));

                Assert.Equal(Client.TlsOutcome.Served, Client.TryGetTls(port, "/", clientCert, clientKey));

                // Replace the issuer with an unrelated self-signed certificate: still a valid
                // anchor file, just not one that admits this client.
                File.WriteAllText(anchors, File.ReadAllText(unrelated));
                service!.ReplaceCertificates(new TlsCertificate
                {
                    CertificatePath = serverCert,
                    KeyPath = serverKey,
                });

                Assert.True(Client.TryGetTls(port, "/", clientCert, clientKey) != Client.TlsOutcome.Served,
                    "the client's issuer was removed from the anchors and the rotation should have applied it");
            }
            finally
            {
                File.Delete(anchors);
            }
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

        RegisterCurlRotation(runner);

        runner.Test("rotate: a validating client is served while rotations run underneath it", () =>
        {
            // Concurrency, driven by curl rather than SslStream. SslStream is the wrong client for
            // this one: it needs a socket receive timeout to bound the loop, and on Linux that
            // timeout interacts with its own async handshake to surface as a cancelled write - a
            // failure inside the client that reads exactly like the server breaking. curl is a
            // separate process with its own timeout, and it validates the certificate as well.
            (string ca, string first, string firstKey) = TestCert.EnsureNamedFromCa("rotate.test");
            (_, string second, string secondKey) = TestCert.EnsureRenewedFromCa("rotate.test");

            TlsService? service = null;
            int port = TestServer.Start(OkHandler, r => service = TlsService.Start(r, new TlsOptions
            {
                CertificatePath = first,
                KeyPath = firstKey,
            }));

            int failed = 0;
            string firstError = "";

            var load = Task.Run(() =>
            {
                for (int i = 0; i < 6; i++)
                {
                    (int exit, _, string stderr) = Curl.Get("rotate.test", port, ca);

                    if (exit != 0)
                    {
                        Interlocked.Increment(ref failed);
                        firstError = firstError.Length == 0 ? stderr : firstError;
                    }
                }
            });

            // Replaced continuously while those requests are mid-handshake, which is the only way
            // to reach the window where a handshake has read one set and not yet used it.
            for (int i = 0; !load.IsCompleted; i++)
            {
                service!.ReplaceCertificates(new TlsCertificate
                {
                    CertificatePath = i % 2 == 0 ? second : first,
                    KeyPath = i % 2 == 0 ? secondKey : firstKey,
                });

                Thread.Sleep(5);
            }

            load.Wait(TimeSpan.FromSeconds(120));

            Assert.True(failed == 0,
                $"{failed}/6 requests failed while certificates were being replaced: {firstError}");
        }, skip: !Curl.Available);

    }

    private static void RegisterCurlRotation(Runner runner)
    {
        runner.Test("control: a validating client is served repeatedly with NO rotation", () =>
        {
            // The control, and it earns its place: when the rotation tests below first flaked, this
            // failed too - which said the problem was the suite's CPU contention rather than
            // anything about replacing certificates. Keep it, so the next person gets that answer
            // in one run instead of an afternoon.
            (string ca, string first, string firstKey) = TestCert.EnsureNamedFromCa("rotate.test");

            int port = TestServer.Start(OkHandler, r => TlsService.Start(r, new TlsOptions
            {
                CertificatePath = first,
                KeyPath = firstKey,
            }));

            for (int i = 0; i < 4; i++)
            {
                (int exit, string body, string stderr) = Curl.Get("rotate.test", port, ca);
                Assert.True(exit == 0, $"request {i} without any rotation failed: {stderr}");
                Assert.True(body.Contains("ok"), $"request {i} was not answered: {body}");
            }
        }, skip: !Curl.Available);

        runner.Test("rotate: a validating client is served correctly across repeated rotations", () =>
        {
            (string ca, string first, string firstKey) = TestCert.EnsureNamedFromCa("rotate.test");
            (_, string second, string secondKey) = TestCert.EnsureRenewedFromCa("rotate.test");

            TlsService? service = null;
            int port = TestServer.Start(OkHandler, r => service = TlsService.Start(r, new TlsOptions
            {
                CertificatePath = first,
                KeyPath = firstKey,
            }));

            // Rotate, then ask - so every single request is served by a set that was installed a
            // moment earlier, and any state a rotation leaves behind is what answers it.
            // Five is enough: each iteration installs a whole new set and the request that follows
            // is served by it. More only lengthens the window in which this suite's other reactors
            // can starve the request, which is a property of the suite and not of rotation.
            for (int i = 0; i < 5; i++)
            {
                bool even = i % 2 == 0;

                service!.ReplaceCertificates(new TlsCertificate
                {
                    CertificatePath = even ? second : first,
                    KeyPath = even ? secondKey : firstKey,
                });

                (int exit, string body, string stderr) = Curl.Get("rotate.test", port, ca);

                Assert.True(exit == 0, $"request {i} after a rotation was refused: {stderr}");
                Assert.True(body.Contains("ok"), $"request {i} after a rotation was not answered: {body}");
            }
        }, skip: !Curl.Available);

        // A curl-driven CONCURRENT rotation test used to live here and was removed: with every
        // test server in the suite still busy-polling, a rotator running underneath the requests
        // only ever measured CPU contention, and failed as a timeout that reads like a server bug.
        // Concurrency is covered in-process by the load test further down, which is far cheaper.

        runner.Test("rotate: a client certificate is still verified across repeated rotations", () =>
        {
            // Both halves at once. The server's certificate is renewed before every request; the
            // client's is checked on every one. Renewing must not disturb who may connect - and the
            // impostor must stay out throughout, or a rotation is a window with no verification.
            (string ca, string serverCert, string serverKey, string clientCert, string clientKey,
             string rogueCert, string rogueKey) = TestCert.EnsureMutualTls();
            (_, string renewed, string renewedKey) = TestCert.EnsureRenewedFromCa("localhost");

            TlsService? service = null;
            int port = TestServer.Start(IdentityHandler, r => service = TlsService.Start(r, new TlsOptions
            {
                CertificatePath = serverCert,
                KeyPath = serverKey,
                ClientCaPath = ca,
                RequireClientCertificate = true,
            }));

            for (int i = 0; i < 4; i++)
            {
                bool even = i % 2 == 0;

                service!.ReplaceCertificates(new TlsCertificate
                {
                    CertificatePath = even ? renewed : serverCert,
                    KeyPath = even ? renewedKey : serverKey,
                });

                (int exit, string body, string stderr) = Curl.Get("localhost", port, ca, "/who", clientCert, clientKey);

                Assert.True(exit == 0, $"the trusted client was refused after rotation {i}: {stderr}");
                Assert.True(body.Contains("alice"),
                    $"the trusted client should still be named after rotation {i}, got: {body}");

                (int rogueExit, _, _) = Curl.Get("localhost", port, ca, "/who", rogueCert, rogueKey);

                Assert.True(rogueExit != 0,
                    $"an untrusted client got in after rotation {i} - a rotation must never be a window with no client verification");
            }
        }, skip: !Curl.Available);
    }

    /// <summary>Answers with the client's verified identity, so authentication can be seen.</summary>
    private static async Task IdentityHandler(Reactor reactor, TcpConnection connection)
    {
        TlsSession? session = null;

        try
        {
            session = await reactor.GetService<TlsService>()!.AcceptAsync(connection);

            // The first request routinely rides in with the handshake's final flight - a TLS 1.3
            // client sends it immediately after Finished, and those bytes are already decrypted and
            // gone from the socket by the time AcceptAsync returns. Answering it BEFORE parking on
            // a read is what the send-first loop means; waiting first hangs, because the bytes
            // being waited for already arrived.
            if (!session.DrainPlaintext().IsEmpty)
            {
                session.Write(connection, Identity(session));
                await connection.FlushAsync();
            }

            // No ResetRead above: that belongs to a read that was actually issued, and calling it
            // for one that never happened leaves the connection's read state describing a read
            // nobody made.
            while (true)
            {
                RecvSnapshot snapshot = await connection.ReadAsync();

                if (snapshot.IsClosed)
                {
                    return;
                }

                byte[] who = System.Text.Encoding.ASCII.GetBytes(session.PeerSubject ?? "anonymous");
                byte[] head = System.Text.Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\ncontent-length: {who.Length}\r\n\r\n");

                session.Write(connection, [.. head, .. who]);

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

    /// <summary>The identity response: who the client proved itself to be.</summary>
    private static byte[] Identity(TlsSession session)
    {
        byte[] who = System.Text.Encoding.ASCII.GetBytes(session.PeerSubject ?? "anonymous");
        byte[] head = System.Text.Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\ncontent-length: {who.Length}\r\n\r\n");

        return [.. head, .. who];
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

            // The first request routinely rides in with the handshake's final flight - a TLS 1.3
            // client sends it immediately after Finished, and those bytes are already decrypted and
            // gone from the socket by the time AcceptAsync returns. Answering it BEFORE parking on
            // a read is what the send-first loop means; waiting first hangs, because the bytes
            // being waited for already arrived.
            if (!session.DrainPlaintext().IsEmpty)
            {
                session.Write(connection, Response);
                await connection.FlushAsync();
            }

            // No ResetRead above: that belongs to a read that was actually issued, and calling it
            // for one that never happened leaves the connection's read state describing a read
            // nobody made.
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
