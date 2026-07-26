using System.Buffers;
using System.Text;
using ioxide;
using ioxide.h3;

namespace Examples.Quic;

/// <summary>
/// One QUIC listener, two protocols, picked by ALPN: "h3" connections get real HTTP/3 through
/// ioxide.h3, anything else gets a raw stream echo through the QuicConnectionDualPipe adapters -
/// the QUIC twin of <see cref="Raw.PipesExample"/>.
///
/// The QUIC handler launches before the handshake, so the ALPN isn't known at entry. The demux
/// awaits the first read WITHOUT draining it (1-RTT stream data implies the handshake finished),
/// peeks <see cref="QuicConnection.NegotiatedProtocol"/>, and hands the still-queued items to
/// whichever layer owns the connection from here - both start by reading the same queue, so
/// nothing is lost. H3Connection sets itself up on its first wake anyway, so the deferral changes
/// no h3 timing.
/// </summary>
public static class QuicH3Example
{
    public static async Task Handle(Reactor r, QuicConnection conn)
    {
        await conn.ReadAsync();   // peek only - items stay queued for the branch below

        if (conn.NegotiatedProtocol == "h3")
        {
            // Owns the handler ref (DecRef on exit). H3Request is bytes throughout - route
            // by byte compare (no per-request strings), decode only what goes into the text.
            await new H3Connection(conn).RunAsync(
                static req => req.Path.Span.SequenceEqual("/plaintext"u8)
                    ? H3Response.Text("Hello, World!")
                    : H3Response.Text($"hello {Encoding.ASCII.GetString(req.Path.Span)} over HTTP/3 via io_uring\n"));
            return;
        }

        await PipeEcho(conn);
    }

    // Raw stream echo over the dual pipe: auto-binds to the client's stream, echoes until the
    // peer's fin, then Complete() half-closes with our own fin. A closed-before-handshake
    // connection falls through here with a completed reader and exits clean.
    private static async Task PipeEcho(QuicConnection conn)
    {
        try
        {
            var pipe = new QuicConnectionDualPipe(conn);

            while (true)
            {
                var result = await pipe.Input.ReadAsync();

                foreach (ReadOnlyMemory<byte> segment in result.Buffer)
                {
                    pipe.Output.Write(segment.Span);
                }
                await pipe.Output.FlushAsync();

                pipe.Input.AdvanceTo(result.Buffer.End);

                if (result.IsCompleted)
                {
                    break;
                }
            }

            pipe.Output.Complete();
            pipe.Input.Complete();
        }
        finally
        {
            conn.DecRef();
        }
    }

    /// <summary>Self-signed localhost cert for the quic-h3 mode (EXAMPLES_QUIC_CERT/KEY override it).</summary>
    public static (string CertPath, string KeyPath) EnsureQuicCert()
    {
        string? envCert = Environment.GetEnvironmentVariable("EXAMPLES_QUIC_CERT");
        string? envKey = Environment.GetEnvironmentVariable("EXAMPLES_QUIC_KEY");
        if (envCert is not null && envKey is not null)
        {
            return (envCert, envKey);
        }

        string dir = Path.Combine(Path.GetTempPath(), "ioxide-examples-quic");
        Directory.CreateDirectory(dir);
        string certPath = Path.Combine(dir, "quic.crt");
        string keyPath = Path.Combine(dir, "quic.key");

        if (!System.IO.File.Exists(certPath))
        {
            using var rsa = System.Security.Cryptography.RSA.Create(2048);
            var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(
                "CN=localhost", rsa, System.Security.Cryptography.HashAlgorithmName.SHA256,
                System.Security.Cryptography.RSASignaturePadding.Pkcs1);
            using var cert = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

            System.IO.File.WriteAllText(certPath, cert.ExportCertificatePem());
            System.IO.File.WriteAllText(keyPath, rsa.ExportPkcs8PrivateKeyPem());
        }

        return (certPath, keyPath);
    }
}
