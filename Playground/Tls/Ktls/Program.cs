using System.Text;
using ioxide;
using ioxide.tls;
using ioxide.utils;
using Playground.Shared;

// ─────────────────────────────────────────────────────────────────────────────────────────────
//  tls-ktls - TLS via ioxide.tls: the OpenSSL handshake runs over the ring, then transmit is
//  handed to KERNEL TLS - the handler writes plaintext and the kernel produces the records, so
//  the send path stays exactly the raw send path.
//
//      sudo modprobe tls                             # needs the Linux 'tls' module + OpenSSL 3
//      dotnet run -c Release --project Playground/Tls/Ktls
//      curl -ks https://127.0.0.1:8443/ | head -c 40
//
//  PLAYGROUND_TLS_CERT/_KEY point at a real PEM pair; otherwise a self-signed localhost cert is
//  generated. PLAYGROUND_BODY sizes the response (default 8 KB - a representative JSON/HTML-ish
//  payload, since TLS overhead only shows against real bodies). Needs: ioxide.tls
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
    KeyPath         = keyPath,
};

// A medium fixed response, built once. TLS cost is per-byte, so "ok" would hide it.
int bodySize = Env.Int("PLAYGROUND_BODY", 8 * 1024);
byte[] body = new byte[bodySize];
ReadOnlySpan<byte> fill = "ioxide-ktls-payload "u8;
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

    // One TlsService per reactor: it owns the OpenSSL contexts and drives handshakes on this ring.
    reactor.OnStart = r => TlsService.Start(r, tlsOptions);

    reactor.TcpHandle = async (r, conn) =>
    {
        TlsSession? tls = null;
        try
        {
            // The handshake reads and writes through this same connection; after it, the socket
            // carries kTLS records the kernel en/decrypts.
            tls = await r.GetService<TlsService>().AcceptAsync(conn);

            // A request can ride in with the handshake's final flight - answer it before parking
            // in ReadAsync, or the client waits on a response we never send.
            if (tls.DrainPlaintext().Length > 0)
            {
                conn.Write(response);
                await conn.FlushAsync();
            }

            while (true)
            {
                RecvSnapshot snapshot = await conn.ReadAsync();

                int got = 0;
                while (conn.TryGetItem(snapshot, out SpscRecvRing.Item item))
                {
                    if (item.HasBuffer)
                    {
                        // Records in, plaintext out - the session decrypts what the ring received.
                        got += DecryptLength(tls, in item);
                        conn.ReturnBuffer(in item);
                    }
                }

                if (got > 0)
                {
                    conn.Write(response);   // plaintext: the kernel encrypts on send
                    await conn.FlushAsync();
                }

                if (snapshot.IsClosed || tls.Closed) return;
                conn.ResetRead();
            }
        }
        catch (Exception e)
        {
            // Handlers run fire-and-forget, so a thrown handshake error would vanish silently -
            // and a missing 'tls' kernel module manifests exactly here.
            Console.Error.WriteLine($"[tls-ktls] connection failed: {e.Message}");
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

Console.WriteLine($"[tls-ktls] {config.ReactorCount} reactors on :{config.Tcp!.Port}, "
                + $"{bodySize}-byte body, cert {certPath}");

foreach (Thread thread in threads)
{
    thread.Join();
}

// Decrypt takes a raw pointer (the buffer belongs to the ring); the pointer work stays out of the
// async handler, which cannot contain unsafe code.
static unsafe int DecryptLength(TlsSession tls, in SpscRecvRing.Item item)
    => tls.Decrypt(item.Ptr, item.Len).Length;
