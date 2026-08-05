using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using ioxide;
using Playground.Shared;

// ─────────────────────────────────────────────────────────────────────────────────────────────
//  tls-sslstream - TLS via the BCL's SslStream over TcpConnectionStream: fully managed,
//  portable, full-featured (TLS 1.2/1.3, client certs, resumption), and with no kTLS or native
//  dependency - every byte is encrypted in userspace, which is also why it is the slower path.
//  Run it against Tls.Ktls to see what the kernel offload buys.
//
//      dotnet run -c Release --project Playground/Tls/SslStream
//      curl -ks https://127.0.0.1:8443/ | head -c 40
//
//  TcpConnectionStream adapts the ioxide connection to a Stream, so anything stream-shaped from
//  the BCL slots on top - SslStream is just the most useful example. Needs: ioxide
// ─────────────────────────────────────────────────────────────────────────────────────────────

(string certPath, string keyPath) = QuicCert.Ensure(
    Env.StrOrNull("PLAYGROUND_TLS_CERT"),
    Env.StrOrNull("PLAYGROUND_TLS_KEY"));
var certificate = X509Certificate2.CreateFromPemFile(certPath, keyPath);

var config = new ServerConfig
{
    ReactorCount = Env.Int("PLAYGROUND_REACTORS", Environment.ProcessorCount),
    Tcp = new TcpOptions
    {
        Port = Env.Port("PLAYGROUND_PORT", 8443),
    },
};

// Same body shape as Tls.Ktls, so the two are directly comparable under the same client.
int bodySize = Env.Int("PLAYGROUND_BODY", 8 * 1024);
byte[] body = new byte[bodySize];
ReadOnlySpan<byte> fill = "ioxide-sslstream-payload "u8;
for (int i = 0; i < bodySize; i++)
{
    body[i] = fill[i % fill.Length];
}
byte[] response =
[
    .. Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {bodySize}\r\n\r\n"),
    .. body,
];

var threads = new Thread[config.ReactorCount];

for (int i = 0; i < threads.Length; i++)
{
    var reactor = new Reactor(i, config);

    reactor.TcpHandle = async (r, conn) =>
    {
        SslStream? ssl = null;
        try
        {
            // The stream adapter reads from the ring and writes into the slab; SslStream never
            // knows it isn't a NetworkStream.
            ssl = new SslStream(new TcpConnectionStream(conn), leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
                ApplicationProtocols = [SslApplicationProtocol.Http11],
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            });

            var request = new byte[8192];
            while (true)
            {
                int n = await ssl.ReadAsync(request);
                if (n == 0) return;                    // peer closed

                await ssl.WriteAsync(response);        // encrypted in userspace
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[tls-sslstream] connection failed: {e.Message}");
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

Console.WriteLine($"[tls-sslstream] {config.ReactorCount} reactors on :{config.Tcp!.Port}, "
                + $"{bodySize}-byte body, cert {certPath}");

foreach (Thread thread in threads)
{
    thread.Join();
}
