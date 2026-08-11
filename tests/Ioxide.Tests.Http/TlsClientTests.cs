using System.Text;
using ioxide;
using ioxide.httpclient;

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
