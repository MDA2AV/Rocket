using System.IO.Pipelines;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using ioxide;
using ioxide.nghttp2;
using Playground.Shared;

// ─────────────────────────────────────────────────────────────────────────────────────────────
//  http2-sslstream - HTTP/2 over the BCL's SslStream instead of kTLS. Fully managed, portable,
//  no kernel module, and slower - every byte is encrypted in userspace both ways.
//
//      dotnet run -c Release --project Playground/Http2SslStream
//      curl -k --http2 https://127.0.0.1:8443/
//
//  The point of this sample is what it demonstrates about the shape: Nghttp2Connection takes an
//  IDuplexPipe, and a Stream can be one in about ten lines (below). So the same HTTP/2 code runs
//  over the ring directly, over kTLS, or over SslStream, without knowing which - the transport is
//  a constructor argument, not a branch inside the protocol.
//
//  Compare with Playground/Http2Tls for the kTLS version. Needs: ioxide, ioxide.nghttp2
// ─────────────────────────────────────────────────────────────────────────────────────────────

(string certPath, string keyPath) = QuicCert.Ensure(
    Env.StrOrNull("PLAYGROUND_TLS_CERT"),
    Env.StrOrNull("PLAYGROUND_TLS_KEY"));
var certificate = X509Certificate2.CreateFromPemFile(certPath, keyPath);
certificate = X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null);

var config = new ServerConfig
{
    ReactorCount = Env.Int("PLAYGROUND_REACTORS", Environment.ProcessorCount),
    Tcp = new TcpOptions
    {
        Port = Env.Port("PLAYGROUND_PORT", 8443),
    },
};

byte[] body = "ok"u8.ToArray();
var threads = new Thread[config.ReactorCount];

for (int i = 0; i < threads.Length; i++)
{
    var reactor = new Reactor(i, config);

    reactor.TcpHandle = async (r, conn) =>
    {
        SslStream? ssl = null;
        try
        {
            ssl = new SslStream(new TcpConnectionStream(conn), leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,

                // Same ordered preference as the kTLS sample: h2 wins when the client offers both.
                ApplicationProtocols = [SslApplicationProtocol.Http2, SslApplicationProtocol.Http11],
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            });

            if (ssl.NegotiatedApplicationProtocol != SslApplicationProtocol.Http2)
            {
                return;   // this sample only serves h2; see Playground/Http2Tls for the fallback
            }

            await new Nghttp2Connection(new StreamDuplexPipe(ssl)).RunBufferedAsync(
                _ => new Nghttp2Response { Status = 200, Body = body });
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[http2-sslstream] connection failed: {e.Message}");
        }
        finally
        {
            ssl?.Dispose();
            conn.DecRef();
        }
    };

    threads[i] = new Thread(reactor.Run) { Name = $"reactor-{i}" };
    threads[i].Start();
}

Console.WriteLine($"[http2-sslstream] {config.ReactorCount} reactors on :{config.Tcp!.Port}, "
                + $"h2 over SslStream (userspace both ways), cert {certPath}");

foreach (Thread thread in threads)
{
    thread.Join();
}

/// <summary>
/// Any Stream as an IDuplexPipe. The BCL already has the two halves - this only pairs them, which
/// is the whole adapter needed to run ioxide's HTTP/2 over something that is not a ring connection.
/// </summary>
internal sealed class StreamDuplexPipe(Stream stream) : IDuplexPipe
{
    public PipeReader Input { get; } = PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true));
    public PipeWriter Output { get; } = PipeWriter.Create(stream, new StreamPipeWriterOptions(leaveOpen: true));
}
