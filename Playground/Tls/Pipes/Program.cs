using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using ioxide;
using ioxide.tls;
using Playground.Shared;

// ─────────────────────────────────────────────────────────────────────────────────────────────
//  tls-pipes - TLS served through an IDuplexPipe, and the point of the sample is what ISN'T here.
//
//      dotnet run -c Release --project Playground/Tls/Pipes                    # kTLS
//      PLAYGROUND_NO_KTLS_TX=1 dotnet run -c Release --project Playground/Tls/Pipes   # OpenSSL
//      curl -ks https://127.0.0.1:8443/ | head -c 40
//
//  Those two commands run the SAME handler code. Not similar - identical. The only difference is
//  one TlsOptions flag, and no branch below it: no "if kernel then write plaintext else encrypt",
//  no second pipe type, no separate loop.
//
//  TlsConnectionDualPipe composes rather than implements. Each direction has two possible halves:
//
//              kernel                       OpenSSL
//    read      TcpConnectionPipeReader      TlsPumpPipeReader
//              (plaintext is already in     (decrypts into a Pipe
//               ring memory - zero copy)     it owns)
//    write     TcpConnectionPipeWriter      TlsEncryptingPipeWriter
//              (plaintext into the slab,    (SSL_write, then the records
//               kernel makes records)        go into the slab)
//
//  It picks them from the SESSION, not from configuration - because the two are not the same
//  thing. TlsOptions is intent; TlsService decides per connection at the handoff, and a handshake
//  that left a partial record cannot hand off to kTLS RX at all, so that connection keeps the
//  userspace reader whatever the config said. TlsSession reports what actually happened, and that
//  is what the pipe reads.
//
//  Compare Playground/Tls/Ktls and Playground/Tls/OpenSsl, which serve the same responses off the
//  RAW ring. Those two differ in the handler, because at that level the backend is visible: kTLS
//  writes plaintext, OpenSSL calls WriteEncrypted. Here it is not. Needs: ioxide
// ─────────────────────────────────────────────────────────────────────────────────────────────

(string certPath, string keyPath) = QuicCert.Ensure(
    Env.StrOrNull("PLAYGROUND_TLS_CERT"),
    Env.StrOrNull("PLAYGROUND_TLS_KEY"));

var config = new ServerConfig
{
    ReactorCount = Env.Int("PLAYGROUND_REACTORS", Environment.ProcessorCount),
    Tcp = new TcpOptions
    {
        Port = Env.Port("PLAYGROUND_PORT", 8443),
    },
};

var tlsOptions = new TlsOptions
{
    CertificatePath = certPath,
    KeyPath = keyPath,

    // The only line that changes between the two runs above.
    KernelTx = !Env.Flag("PLAYGROUND_NO_KTLS_TX"),
};

int bodySize = Env.Int("PLAYGROUND_BODY", 8 * 1024);
byte[] body = new byte[bodySize];
"ioxide-tls-pipes "u8.CopyTo(body);
for (int i = "ioxide-tls-pipes "u8.Length; i < bodySize; i++)
{
    body[i] = (byte)('a' + (i % 26));
}

byte[] response =
[
    .. Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {bodySize}\r\n\r\n"),
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
            Console.Error.WriteLine($"[tls-pipes] connection failed: {e.Message}");
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

Console.WriteLine($"[tls-pipes] {config.ReactorCount} reactors on :{config.Tcp!.Port}, "
                + $"{bodySize}-byte body, tx={(tlsOptions.KernelTx ? "kernel" : "openssl")}");

foreach (Thread thread in threads)
{
    thread.Join();
}
