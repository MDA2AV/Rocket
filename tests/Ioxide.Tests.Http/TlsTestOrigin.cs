using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Ioxide.Tests;

/// <summary>
/// A TLS origin for the client tests: a plain TcpListener wrapped in the BCL's SslStream, speaking
/// just enough HTTP/1.1 to answer a request.
///
/// Deliberately not one of our own servers. ioxide's TLS termination hands transmit to the kernel
/// via kTLS, so it cannot run without the 'tls' module - and testing our client against our own
/// handshake would only prove the two agree with each other. SslStream is an independent
/// implementation, in-process, with no sidecar to install.
/// </summary>
internal sealed class TlsTestOrigin : IDisposable
{
    private readonly TcpListener _listener;
    private readonly X509Certificate2 _certificate;
    private readonly List<SslApplicationProtocol> _alpn;
    private readonly CancellationTokenSource _stopping = new();

    /// <summary>The port it accepted on.</summary>
    public int Port { get; }

    /// <summary>ALPN protocol selected on the most recent handshake, for assertions.</summary>
    public string? LastAlpn { get; private set; }

    private TlsTestOrigin(TcpListener listener, X509Certificate2 certificate, string[] alpn)
    {
        _listener = listener;
        _certificate = certificate;
        _alpn = [.. alpn.Select(p => new SslApplicationProtocol(p))];
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>Start an origin offering <paramref name="alpn"/>, most preferred first.</summary>
    public static TlsTestOrigin Start(params string[] alpn)
    {
        (string certPath, string keyPath) = TestCert.Ensure();
        X509Certificate2 certificate = X509Certificate2.CreateFromPemFile(certPath, keyPath);

        // SslStream on Linux needs the private key associated through a PFX round-trip; a PEM-built
        // certificate carries the key in a form AuthenticateAsServerAsync will not use directly.
        certificate = X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null);

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        var origin = new TlsTestOrigin(listener, certificate, alpn);
        _ = origin.AcceptLoopAsync();
        return origin;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stopping.Token);
            }
            catch
            {
                return;   // stopped
            }

            _ = ServeAsync(client);
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            SslStream? tls = null;
            try
            {
                tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _certificate,
                    ApplicationProtocols = _alpn,
                    ClientCertificateRequired = false,
                });

                LastAlpn = tls.NegotiatedApplicationProtocol.Protocol.Length == 0
                    ? null
                    : tls.NegotiatedApplicationProtocol.ToString();

                var request = new byte[8192];
                while (true)
                {
                    int n = await tls.ReadAsync(request, _stopping.Token);
                    if (n == 0)
                    {
                        return;   // peer closed
                    }

                    string body = $"hello over {LastAlpn ?? "tls"}";
                    byte[] response = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 200 OK\r\n" +
                        $"content-length: {body.Length}\r\n" +
                        "content-type: text/plain\r\n\r\n" +
                        body);

                    await tls.WriteAsync(response, _stopping.Token);
                }
            }
            catch
            {
                // A rejected certificate or a client that walked away mid-handshake is the POINT of
                // several tests, so a failure here is data rather than an error to report.
            }
            finally
            {
                tls?.Dispose();
            }
        }
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _listener.Stop();
        _certificate.Dispose();
        _stopping.Dispose();
    }
}
