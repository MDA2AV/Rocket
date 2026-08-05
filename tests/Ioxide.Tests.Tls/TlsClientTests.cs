using System.Text;
using ioxide;
using ioxide.httpclient;
using ioxide.tls;

namespace Ioxide.Tests;

/// <summary>
/// Client-side TLS: the handshake ioxide had no way to perform at all, so an https:// origin was
/// simply unreachable. These drive it end to end - a pooled HTTP/1.1 client on a reactor, talking
/// to a TLS origin over the ring.
///
/// Unlike the server side these need no kTLS: a client sends small requests and reads whatever it
/// is given, so both directions stay in userspace and the suite runs anywhere.
///
/// The origin is the BCL's SslStream rather than one of our own servers, so a passing test means
/// our client agrees with an independent implementation instead of only with itself.
/// </summary>
internal static class TlsClientTests
{
    private const ushort H2TlsSidecarPort = 14465;

    public static void Register(Runner runner)
    {
        runner.Test("tls client: handshake, request and response over TLS", () =>
        {
            using TlsTestOrigin origin = TlsTestOrigin.Start("http/1.1");
            (string certPath, _) = TestCert.Ensure();

            int proxy = StartProxy(origin.Port, new TlsClientOptions
            {
                ServerName = "localhost",
                AlpnProtocols = ["http/1.1"],
                CaFile = certPath,          // the origin's cert is self-signed, so it is its own root
            });

            (int status, string body) = Client.Get(proxy, "/over-tls", timeoutMs: 20_000);
            Assert.Equal(200, status);
            Assert.Equal("200|hello over http/1.1", body);
        });

        runner.Test("tls client: ALPN offer is negotiated and reported", () =>
        {
            // The origin prefers h2; we offer only http/1.1, so that is what has to come back.
            using TlsTestOrigin origin = TlsTestOrigin.Start("h2", "http/1.1");
            (string certPath, _) = TestCert.Ensure();

            int proxy = StartProxy(origin.Port, new TlsClientOptions
            {
                ServerName = "localhost",
                AlpnProtocols = ["http/1.1"],
                CaFile = certPath,
            });

            (int status, string body) = Client.Get(proxy, "/alpn", timeoutMs: 20_000);
            Assert.Equal(200, status);
            Assert.Equal("200|hello over http/1.1", body);
            Assert.Equal("http/1.1", origin.LastAlpn);
        });

        runner.Test("tls client: an untrusted certificate is refused", () =>
        {
            // Same origin, but no CaFile - so the self-signed certificate is checked against the
            // system trust store, which has never heard of it. This is the test that says
            // verification is real rather than declared.
            using TlsTestOrigin origin = TlsTestOrigin.Start("http/1.1");

            int proxy = StartProxy(origin.Port, new TlsClientOptions
            {
                ServerName = "localhost",
                AlpnProtocols = ["http/1.1"],
            });

            (int status, string body) = Client.Get(proxy, "/untrusted", timeoutMs: 20_000);
            Assert.Equal(200, status);   // the proxy handler answers; the upstream call is what failed
            Assert.True(body.StartsWith("599|"), $"the handshake should have been refused, got: {body}");

            // Assert on the REASON, not just on failure. A bare 599 cannot tell a rejected
            // certificate from a refused connect or a timeout, so it would pass for all the wrong
            // reasons - which is exactly what it used to do before the pool carried the cause
            // through the acquire timeout.
            Assert.True(body.Contains("certificate verify failed"),
                $"should name the verification failure, got: {body}");
        });

        runner.Test("tls client: a certificate for another name is refused", () =>
        {
            // The chain is trusted here (CaFile is the origin's own cert) and the name still has to
            // match. Encrypting to the wrong peer is the failure that matters.
            using TlsTestOrigin origin = TlsTestOrigin.Start("http/1.1");
            (string certPath, _) = TestCert.Ensure();

            int proxy = StartProxy(origin.Port, new TlsClientOptions
            {
                ServerName = "not-the-origin.example",
                AlpnProtocols = ["http/1.1"],
                CaFile = certPath,
            });

            (int status, string body) = Client.Get(proxy, "/wrong-name", timeoutMs: 20_000);
            Assert.Equal(200, status);
            Assert.True(body.StartsWith("599|"), $"the name mismatch should have been refused, got: {body}");
            Assert.True(body.Contains("certificate verify failed"),
                $"should name the verification failure, got: {body}");
        });

        runner.Test("tls client: verification can be turned off deliberately", () =>
        {
            // The documented escape hatch for a self-signed origin in a test. It is here so the
            // difference from the two tests above is visible: the ONLY change is the flag.
            using TlsTestOrigin origin = TlsTestOrigin.Start("http/1.1");

            int proxy = StartProxy(origin.Port, new TlsClientOptions
            {
                ServerName = "not-the-origin.example",
                AlpnProtocols = ["http/1.1"],
                VerifyCertificate = false,
            });

            (int status, string body) = Client.Get(proxy, "/insecure", timeoutMs: 20_000);
            Assert.Equal(200, status);
            Assert.Equal("200|hello over http/1.1", body);
        });

        // A real HTTP/2-over-TLS origin, which SslStream cannot stand in for: it can negotiate the
        // ALPN token but not speak the framing behind it. nginx does both.
        //
        //   docker run -d --name nginx-h2-tls --network host \
        //     -v .../nginx-h2-tls.conf:/etc/nginx/nginx.conf:ro \
        //     -v .../doc_root:/doc_root:ro -v /tmp/ioxide-e2e-tls:/certs:ro nginx
        bool noH2Tls = !Sidecars.Reachable("127.0.0.1", H2TlsSidecarPort);

        runner.Test("tls client: h2 over TLS against a real HTTP/2 origin", () =>
        {
            (string certPath, _) = TestCert.Ensure();

            using TlsClientContext tls = TlsClientContext.Create(new TlsClientOptions
            {
                ServerName = "localhost",
                AlpnProtocols = ["h2", "http/1.1"],
                CaFile = certPath,
            });

            int proxy = TestServer.Start(Http2ProxyHandler, onStart: reactor =>
                Http2ClientPool.Start(reactor, new Http2ClientOptions
                {
                    Host = "127.0.0.1",
                    Port = H2TlsSidecarPort,
                    PoolSize = 1,
                    AcquireTimeoutMs = 5_000,
                    Tls = tls,
                }));

            (int status, string body) = Client.Get(proxy, "/index.html", timeoutMs: 20_000);
            Assert.Equal(200, status);

            // The sidecar's 1 KiB object, fetched over HTTP/2 inside TLS with the chain verified.
            Assert.True(body.StartsWith("200|"), $"upstream should have answered 200, got: {body[..Math.Min(40, body.Length)]}");
            Assert.Equal(1024, body.Length - "200|".Length);
        }, skip: noH2Tls);

        runner.Test("tls client: h2 over TLS refuses an origin that did not select h2", () =>
        {
            // Over TLS, HTTP/2 is chosen by ALPN and by nothing else. An origin that picked
            // http/1.1 will not understand the HTTP/2 preface, and sending it anyway produces a
            // connection that hangs instead of an error anyone can read - so the connect fails
            // here, while there is still something useful to say.
            using TlsTestOrigin origin = TlsTestOrigin.Start("http/1.1");
            (string certPath, _) = TestCert.Ensure();

            // Offer BOTH. Offering only "h2" to an http/1.1-only origin fails the HANDSHAKE with
            // no_application_protocol, which never reaches the guard being tested - the connection
            // has to succeed and negotiate http/1.1 for the post-handshake check to fire.
            using TlsClientContext tls = TlsClientContext.Create(new TlsClientOptions
            {
                ServerName = "localhost",
                AlpnProtocols = ["h2", "http/1.1"],
                CaFile = certPath,
            });

            int proxy = TestServer.Start(Http2ProxyHandler, onStart: reactor =>
                Http2ClientPool.Start(reactor, new Http2ClientOptions
                {
                    Host = "127.0.0.1",
                    Port = (ushort)origin.Port,
                    PoolSize = 1,
                    AcquireTimeoutMs = 3_000,
                    Tls = tls,
                }));

            (int status, string body) = Client.Get(proxy, "/h2-mismatch", timeoutMs: 20_000);
            Assert.Equal(200, status);
            Assert.True(body.StartsWith("599|"), $"the h2 connect should have failed, got: {body}");
            Assert.True(body.Contains("ALPN") || body.Contains("alpn"),
                $"should be the ALPN guard rather than a handshake failure, got: {body}");
        });

        runner.Test("tls client: ServerName is required", () =>
        {
            // Without a name there is nothing to send as SNI and nothing to check the certificate
            // against, so this fails loudly at construction rather than silently unverified.
            bool threw = false;
            try
            {
                using TlsClientContext _ = TlsClientContext.Create(new TlsClientOptions { ServerName = "" });
            }
            catch (ArgumentException)
            {
                threw = true;
            }

            Assert.True(threw, "an empty ServerName must be rejected");
        });
    }

    // A TCP endpoint whose handler fetches from the TLS origin through the pooled client, and
    // writes back "<upstream status>|<detail>" - the same shape the other client suites use.
    private static int StartProxy(int originPort, TlsClientOptions tlsOptions)
    {
        TlsClientContext tls = TlsClientContext.Create(tlsOptions);

        var options = new HttpClientOptions
        {
            Host = "127.0.0.1",
            Port = (ushort)originPort,
            PoolSize = 1,
            AcquireTimeoutMs = 5_000,
            Tls = tls,
        };

        return TestServer.Start(ProxyHandler, onStart: reactor => HttpClientPool.Start(reactor, options));
    }

    // The same shape as ProxyHandler, driving the HTTP/2 pool instead.
    private static async Task Http2ProxyHandler(Reactor reactor, TcpConnection connection)
    {
        try
        {
            Http2ClientPool upstream = reactor.GetService<Http2ClientPool>()!;

            while (true)
            {
                RecvSnapshot snapshot = await connection.ReadAsync();
                if (snapshot.IsClosed)
                {
                    return;
                }
                string path = Wire.ReadPath(connection, snapshot);

                string detail;
                int status;
                try
                {
                    using HttpClientResponse response = await upstream.GetAsync(path);
                    status = response.Status;
                    detail = Encoding.ASCII.GetString(response.Body.Span);
                }
                catch (Exception e)
                {
                    status = 599;
                    detail = e.Message;
                }

                Wire.Write(connection, 200, $"{status}|{detail}");
                await connection.FlushAsync();
                connection.ResetRead();
            }
        }
        finally
        {
            connection.DecRef();
        }
    }

    private static async Task ProxyHandler(Reactor reactor, TcpConnection connection)
    {
        try
        {
            HttpClientPool upstream = reactor.GetService<HttpClientPool>()!;

            while (true)
            {
                RecvSnapshot snapshot = await connection.ReadAsync();
                if (snapshot.IsClosed)
                {
                    return;
                }
                string path = Wire.ReadPath(connection, snapshot);

                string detail;
                int status;
                try
                {
                    using HttpClientResponse response = await upstream.GetAsync(path);
                    status = response.Status;
                    detail = Encoding.ASCII.GetString(response.Body.Span);
                }
                catch (Exception e)
                {
                    status = 599;
                    detail = e.Message;
                }

                Wire.Write(connection, 200, $"{status}|{detail}");
                await connection.FlushAsync();
                connection.ResetRead();
            }
        }
        finally
        {
            connection.DecRef();
        }
    }
}
