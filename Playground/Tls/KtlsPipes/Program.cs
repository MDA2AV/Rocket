using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using ioxide;
using ioxide.tls;
using Playground.Shared;

// ─────────────────────────────────────────────────────────────────────────────────────────────
//  tls-ktls-pipes - TLS served through an IDuplexPipe, with the kernel doing the transmit
//  crypto - the hybrid, same as Tls/Ktls; kernelRx = true is full kTLS (experimental).
//
//      sudo modprobe tls        # kTLS needs the module
//      curl -ks https://127.0.0.1:8443/ | head -c 40
//
//  The read/serve loop below is byte-for-byte identical to Playground/Tls/OpenSslPipes - diff
//  them and the only functional difference is the KernelTx line, the rest being the banner and
//  the log tag.
//
//  TlsConnectionDualPipe composes rather than implements. Each direction has two possible halves:
//
//              kernel                       OpenSSL
//    read      TcpConnectionPipeReader      TlsDecryptingPipeReader
//              (plaintext is already in     (decrypts into a Pipe
//               ring memory - zero copy)     it owns)
//    write     TcpConnectionPipeWriter      TlsEncryptingPipeWriter
//              (plaintext into the slab,    (SSL_write, then the records
//               kernel makes records)        go into the slab)
//
//  It picks them from the SESSION, not from configuration - because the two are not the same
//  thing. TlsOptions is intent; TlsService decides per connection at the handoff, and a handshake
//  that left a partial record cannot hand off to kTLS RX at all, so that connection keeps the
//  userspace reader whatever the config said. TlsSession reports what actually happened.
//
//  Compare the raw-ring pair, Playground/Tls/Ktls and Playground/Tls/OpenSsl. Those two DO differ
//  in the handler, because at that level the backend is visible: kTLS writes plaintext straight
//  to the connection, OpenSSL goes through TlsSession.Write. Over a pipe it is not. Needs: ioxide
// ─────────────────────────────────────────────────────────────────────────────────────────────

// ── Knobs ────────────────────────────────────────────────────────────────────────────────────
// Edit these. That is the whole mechanism - there is no config file and nothing else to find.
// Env.Override exists only so bench/run.sh can drive the sample from outside; delete that line
// when you copy this out and the literals above it are the entire configuration.

ushort port      = 8443;                        // https://127.0.0.1:8443/
int    reactors  = Environment.ProcessorCount;  // one ring per reactor, one reactor per core
int    bodyBytes = 8 * 1024;                    // TLS cost is per-byte, so a 2-byte "ok" would hide it

Env.Override(ref port, ref reactors, ref bodyBytes);

// A real PEM pair, or null to generate a self-signed localhost cert on first run.
string? certOverride = null;
string? keyOverride  = null;

// Hand INBOUND decryption to the kernel too. Off by default and experimental: about one first
// connection in twelve fails outright, and a client sending a TLS 1.3 KeyUpdate loses the
// connection. Transmit stays kernel-side either way - that is the point of this sample.
bool kernelRx = false;

Env.OverrideKtls(ref kernelRx);
// ─────────────────────────────────────────────────────────────────────────────────────────────

(string certPath, string keyPath) = QuicCert.Ensure(certOverride, keyOverride);

var config = new ServerConfig
{
    ReactorCount = reactors,
    Tcp = new TcpOptions
    {
        Port = port,
    },
};

var tlsOptions = new TlsOptions
{
    CertificatePath = certPath,
    KeyPath = keyPath,

    // NOT the default - TLS is OpenSSL both ways unless you ask for this. As shipped this is the
    // mixed mode, kernel TX with OpenSSL RX; Playground/Tls/OpenSslPipes is the same server
    // without this line.
    KernelTx = true,

    KernelRx = kernelRx,
};

byte[] body = new byte[bodyBytes];
"ioxide-tls-pipes "u8.CopyTo(body);
for (int i = "ioxide-tls-pipes "u8.Length; i < bodyBytes; i++)
{
    body[i] = (byte)('a' + (i % 26));
}

byte[] response =
[
    .. Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {bodyBytes}\r\n\r\n"),
    .. body,
];

var threads = new Thread[config.ReactorCount];

for (int i = 0; i < threads.Length; i++)
{
    var reactor = new Reactor(i, config);

    reactor.OnStart = r => TlsService.Start(r, tlsOptions);

    reactor.TcpHandle = async (r, conn) =>
    {
        TlsSession? tls = null;
        try
        {
            tls = await r.GetService<TlsService>().AcceptAsync(conn);

            // From here down, nothing knows or cares which backend is in use.
            await using var pipe = new TlsConnectionDualPipe(conn, tls, ownsSession: false);

            while (true)
            {
                ReadResult read = await pipe.Input.ReadAsync();

                // Answer per REQUEST, not per read. TLS hands back RECORDS, so one request split
                // across two records would otherwise draw two responses.
                int answered = 0;
                SequencePosition consumed = read.Buffer.Start;

                var reader = new SequenceReader<byte>(read.Buffer);
                while (reader.TryReadTo(out ReadOnlySequence<byte> _, "\r\n\r\n"u8, advancePastDelimiter: true))
                {
                    consumed = reader.Position;
                    answered++;
                }

                // Consumed only whole requests; examined everything, so a partial head parks
                // until more arrives instead of spinning on the same bytes.
                pipe.Input.AdvanceTo(consumed, read.Buffer.End);

                for (int n = 0; n < answered; n++)
                {
                    pipe.Output.Write(response);
                }

                if (answered > 0)
                {
                    await pipe.Output.FlushAsync();
                }

                if (read.IsCompleted || read.IsCanceled)
                {
                    return;
                }
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[tls-ktls-pipes] connection failed: {e.Message}");
        }
        finally
        {
            tls?.Dispose();
            conn.DecRef();
        }
    };

    threads[i] = new Thread(reactor.Run) { Name = $"reactor-{i}" };
    threads[i].Start();
}

Console.WriteLine($"[tls-ktls-pipes] {config.ReactorCount} reactors on :{config.Tcp!.Port}, "
                + $"{bodyBytes}-byte body, tx={(tlsOptions.KernelTx ? "kernel" : "openssl")}, "
                + $"rx={(kernelRx ? "kernel (experimental)" : "openssl")}");

foreach (Thread thread in threads)
{
    thread.Join();
}
