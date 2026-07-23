using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using ioxide;

namespace Examples.Tls;

/// <summary>
/// TLS via the BCL SslStream over ConnectionStream: fully managed, portable, full-featured
/// (TLS 1.2 and 1.3, client certs, resumption), no kTLS or native dependency. Slower than the
/// kTLS path - everything is encrypted in userspace.
/// </summary>
public static class SslStreamExample
{
    private static X509Certificate2 _cert = null!;

    public static void Init(X509Certificate2 cert) => _cert = cert;

    public static async Task Handle(Reactor r, TcpConnection conn)
    {
        SslStream? ssl = null;
        try
        {
            ssl = new SslStream(new ConnectionStream(conn), leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = _cert,
                ApplicationProtocols = [SslApplicationProtocol.Http11],
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            });

            var request = new byte[8192];
            while (true)
            {
                int n = await ssl.ReadAsync(request);
                if (n == 0)
                {
                    return;   // peer closed
                }
                await ssl.WriteAsync(Body.Response);   // SslStream encrypts in userspace
            }
        }
        finally
        {
            ssl?.Dispose();
            conn.DecRef();
        }
    }
}
