using ioxide;
using ioxide.tls;

namespace Ioxide.Tests;

/// <summary>
/// Server Name Indication on the TCP side: one listener answering for several hosts, each with its
/// own certificate.
/// </summary>
/// <remarks>
/// Driven with SslStream as the client, so the name goes out as a real SNI extension and the
/// certificate that comes back is read the way any client reads it. Every case asserts on the
/// SUBJECT of the certificate served, because that is the only externally visible effect of the
/// selection - a server that always answered with the same certificate would fail these and pass
/// anything weaker.
/// </remarks>
internal static class SniTests
{
    private static readonly byte[] Response = "HTTP/1.1 200 OK\r\ncontent-length: 2\r\n\r\nok"u8.ToArray();

    public static void Register(Runner runner, bool ktls)
    {
        foreach ((string label, bool kernelTx) in Paths(ktls))
        {
            runner.Test($"sni{label}: a named host is answered with its own certificate", () =>
            {
                int port = StartTwoHosts(kernelTx);

                Assert.True(Client.ServerCertificateSubject(port, "alpha.test").Contains("alpha.test"),
                    "asking for alpha.test should be answered with alpha.test's certificate");

                Assert.True(Client.ServerCertificateSubject(port, "beta.test").Contains("beta.test"),
                    "asking for beta.test should be answered with beta.test's certificate");
            });

            runner.Test($"sni{label}: a request over a named host is answered", () =>
            {
                // The certificate is only half of it. A host context that is missing anything the
                // connection needs AFTER selection - its keylog callback, which is what the kTLS
                // handoff reads the traffic secret from - still hands back the right certificate
                // and then fails, so this asks the connection to actually do something. Under the
                // ktls label above, this is the only test that exercises kTLS on a selected host.
                int port = StartTwoHosts(kernelTx);

                (string subject, _, int status, string body) = Client.GetTlsSni(port, "/", "alpha.test");

                Assert.True(subject.Contains("alpha.test"), $"wrong certificate: {subject}");
                Assert.Equal(200, status);
                Assert.True(body.Contains("ok"), $"the named host should answer the request, got: {body}");
            });

            runner.Test($"sni{label}: a name that merely starts with a registered one gets the default", () =>
            {
                int port = StartTwoHosts(kernelTx);

                // The matcher compares lengths before bytes, so this cannot match - but an
                // "optimisation" to a prefix compare would hand alpha.test's certificate to
                // whoever asked for a name that merely begins with it.
                Assert.True(Client.ServerCertificateSubject(port, "alpha.test.evil.example").Contains("localhost"),
                    "a longer name that starts with a registered one must not match it");
            });

            runner.Test($"sni{label}: a client sending no name gets the default certificate", () =>
            {
                int port = StartTwoHosts(kernelTx);

                Assert.True(Client.ServerCertificateSubject(port, null).Contains("localhost"),
                    "a client that sends no SNI extension should get the default certificate");
            });

            runner.Test($"sni{label}: an unknown name gets the default rather than a refusal", () =>
            {
                int port = StartTwoHosts(kernelTx);

                // Answering is deliberate. Aborting the handshake would report a name this server
                // does not hold as a dead connection, and would break reaching it by address.
                Assert.True(Client.ServerCertificateSubject(port, "nobody.test").Contains("localhost"),
                    "an unlisted name should fall back to the default certificate, not fail");
            });

            runner.Test($"sni{label}: the name is matched case-insensitively", () =>
            {
                int port = StartTwoHosts(kernelTx);

                // DNS names are case-insensitive, so a client shouting is asking for the same host.
                Assert.True(Client.ServerCertificateSubject(port, "ALPHA.test").Contains("alpha.test"),
                    "ALPHA.test and alpha.test are the same host");
            });
        }

        runner.Test("sni: a host configured under its uppercase name is still matched", () =>
        {
            // The other direction from the case-insensitivity test above: the CONFIGURED name is
            // folded too, so a table written in whatever case still answers.
            (string cert, string key) = TestCert.Ensure();
            (string alphaCert, string alphaKey) = TestCert.EnsureNamed("alpha.test");

            int port = TestServer.Start(OkHandler, r => TlsService.Start(r, new TlsOptions
            {
                CertificatePath = cert,
                KeyPath = key,
                CertificatesByHost = new Dictionary<string, TlsCertificate>
                {
                    ["ALPHA.TEST"] = new() { CertificatePath = alphaCert, KeyPath = alphaKey },
                },
            }));

            Assert.True(Client.ServerCertificateSubject(port, "alpha.test").Contains("alpha.test"),
                "a host configured as ALPHA.TEST should answer for alpha.test");
        });

        runner.Test("sni: with no table configured the default certificate answers everything", () =>
        {
            // The regression guard for everyone not using SNI: no servername callback is installed
            // at all, and a named request is answered exactly as it was before.
            (string cert, string key) = TestCert.Ensure();

            int port = TestServer.Start(OkHandler, r => TlsService.Start(r, new TlsOptions
            {
                CertificatePath = cert,
                KeyPath = key,
            }));

            Assert.True(Client.ServerCertificateSubject(port, "alpha.test").Contains("localhost"),
                "without a table the default certificate answers every name");
        });

        runner.Test("sni: the names a service answers for are reported", () =>
        {
            (string cert, string key) = TestCert.Ensure();
            (string alphaCert, string alphaKey) = TestCert.EnsureNamed("alpha.test");

            TlsService? service = null;

            TestServer.Start(OkHandler, r => service = TlsService.Start(r, new TlsOptions
            {
                CertificatePath = cert,
                KeyPath = key,
                CertificatesByHost = new Dictionary<string, TlsCertificate>
                {
                    ["Alpha.Test"] = new() { CertificatePath = alphaCert, KeyPath = alphaKey },
                },
            }));

            Assert.True(service is not null, "the service should have started");
            Assert.True(service!.ServerNames.Contains("alpha.test"),
                $"the configured name should be reported, lowercased; got: {string.Join(",", service.ServerNames)}");
        });

        // Mutual TLS and SNI together. This is the pair worth testing rather than either alone: the
        // handshake picks a certificate from the name the client asked for, and the question is
        // whether asking for a name can also pick a way out of proving who you are.
        runner.Test("sni: selecting a named host still demands a client certificate", () =>
        {
            (string ca, string serverCert, string serverKey, _, _, _, _) = TestCert.EnsureMutualTls();
            (string alphaCert, string alphaKey) = TestCert.EnsureNamed("alpha.test");

            int port = TestServer.Start(OkHandler, r => TlsService.Start(r, new TlsOptions
            {
                CertificatePath = serverCert,
                KeyPath = serverKey,
                ClientCaPath = ca,
                RequireClientCertificate = true,
                CertificatesByHost = new Dictionary<string, TlsCertificate>
                {
                    ["alpha.test"] = new() { CertificatePath = alphaCert, KeyPath = alphaKey },
                },
            }));

            bool refused;

            try
            {
                // A named host, and nothing to prove who is asking.
                Client.GetTlsClientCert(port, "/", null, null, host: "alpha.test");
                refused = false;
            }
            catch (Exception)
            {
                refused = true;
            }

            Assert.True(refused,
                "asking for a host by name must not be a way around the client certificate the server requires");
        });

        runner.Test("sni: a named host verifies the client against the same anchors", () =>
        {
            (string ca, string serverCert, string serverKey, string clientCert, string clientKey,
             string rogueCert, string rogueKey) = TestCert.EnsureMutualTls();
            (string alphaCert, string alphaKey) = TestCert.EnsureNamed("alpha.test");

            int port = TestServer.Start(OkHandler, r => TlsService.Start(r, new TlsOptions
            {
                CertificatePath = serverCert,
                KeyPath = serverKey,
                ClientCaPath = ca,
                RequireClientCertificate = true,
                CertificatesByHost = new Dictionary<string, TlsCertificate>
                {
                    ["alpha.test"] = new() { CertificatePath = alphaCert, KeyPath = alphaKey },
                },
            }));

            // Trusted client, named host: gets in, and gets alpha.test's certificate.
            (int status, _) = Client.GetTlsClientCert(port, "/", clientCert, clientKey, host: "alpha.test");
            Assert.Equal(200, status);

            // Same named host, a certificate from a CA this server never trusted: still turned away.
            bool rogueRefused;

            try
            {
                Client.GetTlsClientCert(port, "/", rogueCert, rogueKey, host: "alpha.test");
                rogueRefused = false;
            }
            catch (Exception)
            {
                rogueRefused = true;
            }

            Assert.True(rogueRefused,
                "a named host must validate client certificates against the same anchors as the default");
        });

        runner.Test("sni: ALPN is negotiated on a host chosen by name", () =>
        {
            // The case SNI exists for - one address, several hosts, HTTP/2 - and the one that
            // breaks silently: ALPN is settled against whichever context the handshake ended on,
            // so a host context without the callback negotiates nothing and the client is left
            // holding a certificate it cannot speak to.
            (string cert, string key) = TestCert.Ensure();
            (string alphaCert, string alphaKey) = TestCert.EnsureNamed("alpha.test");

            int port = TestServer.Start(OkHandler, r => TlsService.Start(r, new TlsOptions
            {
                CertificatePath = cert,
                KeyPath = key,
                Alpn = ["h2", "http/1.1"],
                CertificatesByHost = new Dictionary<string, TlsCertificate>
                {
                    ["alpha.test"] = new() { CertificatePath = alphaCert, KeyPath = alphaKey },
                },
            }));

            // Client offers both, least-preferred first: the server's order has to decide, not the
            // client's, or this passes for the wrong reason.
            (string subject, string alpn, _, _) =
                Client.GetTlsSni(port, "/", "alpha.test", alpn: ["http/1.1", "h2"]);

            Assert.True(subject.Contains("alpha.test"), $"wrong certificate: {subject}");
            Assert.Equal("h2", alpn);
        });

        runner.Test("sni: a host entry given PEM text is served", () =>
        {
            // The in-memory route, for a host whose material comes from a secrets store rather
            // than a file. Only its REFUSAL was covered - nothing showed it working.
            (string cert, string key) = TestCert.Ensure();
            (string alphaCert, string alphaKey) = TestCert.EnsureNamed("alpha.test");

            int port = TestServer.Start(OkHandler, r => TlsService.Start(r, new TlsOptions
            {
                CertificatePath = cert,
                KeyPath = key,
                CertificatesByHost = new Dictionary<string, TlsCertificate>
                {
                    ["alpha.test"] = new()
                    {
                        CertificatePem = File.ReadAllText(alphaCert),
                        KeyPem = File.ReadAllText(alphaKey),
                    },
                },
            }));

            Assert.True(Client.ServerCertificateSubject(port, "alpha.test").Contains("alpha.test"),
                "a host configured from PEM text should be served like any other");
        });

        runner.Test("sni: the right host answers out of a table of many", () =>
        {
            // Two entries never exercise a scan. Twelve do, and h1/h11/h12 share a prefix, so an
            // off-by-one at either end shows up as the wrong certificate rather than as nothing.
            (string cert, string key) = TestCert.Ensure();

            var table = new Dictionary<string, TlsCertificate>();
            for (int i = 1; i <= 12; i++)
            {
                (string hostCert, string hostKey) = TestCert.EnsureNamed($"h{i}.test");
                table[$"h{i}.test"] = new() { CertificatePath = hostCert, KeyPath = hostKey };
            }

            int port = TestServer.Start(OkHandler, r => TlsService.Start(r, new TlsOptions
            {
                CertificatePath = cert,
                KeyPath = key,
                CertificatesByHost = table,
            }));

            // Asserted with the CN= prefix, so h1 cannot be satisfied by h11's certificate.
            Assert.True(Client.ServerCertificateSubject(port, "h1.test").Contains("CN=h1.test"),
                "the first entry should get its own certificate");
            Assert.True(Client.ServerCertificateSubject(port, "h11.test").Contains("CN=h11.test"),
                "a name sharing a prefix with another should get its own certificate");
            Assert.True(Client.ServerCertificateSubject(port, "h12.test").Contains("CN=h12.test"),
                "the last entry should be reachable");
        });

        runner.Test("sni: a blank host name is refused", () =>
        {
            (string cert, string key) = TestCert.Ensure();
            (string alphaCert, string alphaKey) = TestCert.EnsureNamed("alpha.test");

            Assert.True(StartFails(new TlsOptions
            {
                CertificatePath = cert,
                KeyPath = key,
                CertificatesByHost = new Dictionary<string, TlsCertificate>
                {
                    ["  "] = new() { CertificatePath = alphaCert, KeyPath = alphaKey },
                },
            }, "A blank host cannot be asked for by SNI"), "a blank name cannot be asked for and should be refused");
        });

        runner.Test("sni: a host entry naming no certificate source is refused", () =>
        {
            (string cert, string key) = TestCert.Ensure();
            (_, string alphaKey) = TestCert.EnsureNamed("alpha.test");

            // Neither source is as wrong as both, and reads as an entry someone forgot to finish.
            Assert.True(StartFails(new TlsOptions
            {
                CertificatePath = cert,
                KeyPath = key,
                CertificatesByHost = new Dictionary<string, TlsCertificate>
                {
                    ["alpha.test"] = new() { KeyPath = alphaKey },
                },
            }, "Exactly one certificate source for 'alpha.test'"), "a host entry with no certificate should be refused");
        });

        runner.Test("sni: a host entry naming two key sources is refused", () =>
        {
            (string cert, string key) = TestCert.Ensure();
            (string alphaCert, string alphaKey) = TestCert.EnsureNamed("alpha.test");

            Assert.True(StartFails(new TlsOptions
            {
                CertificatePath = cert,
                KeyPath = key,
                CertificatesByHost = new Dictionary<string, TlsCertificate>
                {
                    ["alpha.test"] = new()
                    {
                        CertificatePath = alphaCert,
                        KeyPath = alphaKey,
                        KeyPem = File.ReadAllText(alphaKey),
                    },
                },
            }, "Exactly one key source for 'alpha.test'"), "two key sources for one host should be refused");
        });

        runner.Test("sni: an unreadable host certificate fails at startup", () =>
        {
            (string cert, string key) = TestCert.Ensure();

            Assert.True(StartFails(new TlsOptions
            {
                CertificatePath = cert,
                KeyPath = key,
                CertificatesByHost = new Dictionary<string, TlsCertificate>
                {
                    ["alpha.test"] = new() { CertificatePath = "/nonexistent/alpha.crt", KeyPath = key },
                },
            }, "alpha.test"), "a host certificate that cannot be read should fail at startup, not at a handshake");
        });

        runner.Test("sni: two entries for the same host are refused", () =>
        {
            (string cert, string key) = TestCert.Ensure();
            (string alphaCert, string alphaKey) = TestCert.EnsureNamed("alpha.test");

            string refusal = "";

            try
            {
                TestServer.Start(OkHandler, r => TlsService.Start(r, new TlsOptions
                {
                    CertificatePath = cert,
                    KeyPath = key,
                    CertificatesByHost = new Dictionary<string, TlsCertificate>
                    {
                        // Distinct keys to the dictionary, the same host to the handshake. Only the
                        // first would ever be served, so the second is a certificate that silently
                        // never answers - refused rather than shadowed.
                        ["alpha.test"] = new() { CertificatePath = alphaCert, KeyPath = alphaKey },
                        ["ALPHA.test"] = new() { CertificatePath = cert, KeyPath = key },
                    },
                }));
            }
            catch (Exception e)
            {
                refusal = e.Message;
            }

            Assert.True(refusal.Contains("Two certificates for the same host"),
                $"two entries folding to one name should be refused at startup; got: {refusal}");
        });

        runner.Test("sni: a host entry naming two certificate sources is refused", () =>
        {
            (string cert, string key) = TestCert.Ensure();
            (string alphaCert, string alphaKey) = TestCert.EnsureNamed("alpha.test");

            string refusal = "";

            try
            {
                TestServer.Start(OkHandler, r => TlsService.Start(r, new TlsOptions
                {
                    CertificatePath = cert,
                    KeyPath = key,
                    CertificatesByHost = new Dictionary<string, TlsCertificate>
                    {
                        // Both a path and text: two answers to one question.
                        ["alpha.test"] = new()
                        {
                            CertificatePath = alphaCert,
                            CertificatePem = File.ReadAllText(alphaCert),
                            KeyPath = alphaKey,
                        },
                    },
                }));
            }
            catch (Exception e)
            {
                // The reactor raises it on its own thread and the harness reports the failure to
                // start, so what is asserted is the reason rather than the exception type.
                refusal = e.Message;
            }

            Assert.True(refusal.Contains("Exactly one certificate source for 'alpha.test'"),
                $"naming two certificate sources for one host should be refused at startup; got: {refusal}");
        });
    }

    /// <summary>
    /// Whether starting with these options was refused for the stated reason. The reactor raises on
    /// its own thread and the harness reports the failure to start, so the assertion is on the
    /// reason rather than on the exception type.
    /// </summary>
    private static bool StartFails(TlsOptions options, string because)
    {
        try
        {
            TestServer.Start(OkHandler, r => TlsService.Start(r, options));
            return false;
        }
        catch (Exception e)
        {
            return e.Message.Contains(because);
        }
    }

    /// <summary>Default localhost certificate, plus alpha.test and beta.test by name.</summary>
    private static int StartTwoHosts(bool kernelTx)
    {
        (string cert, string key) = TestCert.Ensure();
        (string alphaCert, string alphaKey) = TestCert.EnsureNamed("alpha.test");
        (string betaCert, string betaKey) = TestCert.EnsureNamed("beta.test");

        return TestServer.Start(OkHandler, r => TlsService.Start(r, new TlsOptions
        {
            CertificatePath = cert,
            KeyPath = key,
            KernelTx = kernelTx,
            CertificatesByHost = new Dictionary<string, TlsCertificate>
            {
                ["alpha.test"] = new() { CertificatePath = alphaCert, KeyPath = alphaKey },
                ["beta.test"] = new() { CertificatePath = betaCert, KeyPath = betaKey },
            },
        }));
    }

    /// <summary>
    /// Completes the handshake and answers, so a connection is not torn down before the client has
    /// read the certificate. What it serves does not matter here - the certificate does.
    /// </summary>
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
