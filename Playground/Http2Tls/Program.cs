using System.IO.Pipelines;
using System.Text;
using ioxide;
using ioxide.nghttp2;
using ioxide.tls;
using Playground.Shared;

// ─────────────────────────────────────────────────────────────────────────────────────────────
//  http2-tls - HTTP/2 over TLS, negotiated by ALPN, alongside HTTP/1.1 on the SAME port. This is
//  what a browser expects: it offers "h2,http/1.1" and the server chooses.
//
//      dotnet run -c Release --project Playground/Http2Tls
//      curl -k --http2 https://127.0.0.1:8443/          # negotiates h2
//      curl -k --http1.1 https://127.0.0.1:8443/        # same port, gets http/1.1
//
//  Two things make this work. TlsOptions.Alpn is an ORDERED list, most preferred first, and the
//  server walks it to pick the first entry the client also offered - so the order below is the
//  policy. And TlsSession.NegotiatedAlpn reports what was chosen, which is what lets one handler
//  run two different protocol loops.
//
//  Note what Nghttp2Connection is handed: a TlsConnectionDualPipe. It never learns that TLS is
//  involved - the pipe decrypts on the way in, and kTLS encrypts on the way out, so the protocol
//  code is byte-for-byte the same as the h2c sample. Needs: ioxide, ioxide.nghttp2
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

byte[] body = "ok"u8.ToArray();
byte[] http11Response =
[
    .. Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\n\r\n"),
    .. body,
];

var threads = new Thread[config.ReactorCount];

for (int i = 0; i < threads.Length; i++)
{
    var reactor = new Reactor(i, config);

    reactor.OnStart = r => TlsService.Start(r, new TlsOptions
    {
        CertificatePath = certPath,
        KeyPath = keyPath,

        // Most preferred first. ALPN has no weights - RFC 7301 carries a plain ordered list - so
        // position IS the preference, and a client offering both lands on h2.
        Alpn = ["h2", "http/1.1"],
    });

    reactor.TcpHandle = async (r, conn) =>
    {
        TlsSession? tls = null;
        try
        {
            tls = await r.GetService<TlsService>()!.AcceptAsync(conn);

            if (tls.NegotiatedAlpn == "h2")
            {
                // The decrypt pump lives in the pipe, so the HTTP/2 code below is identical to the
                // cleartext sample.
                await using var pipe = new TlsConnectionDualPipe(conn, tls, ownsSession: false);
                await new Nghttp2Connection(pipe).RunBufferedAsync(_ => new Nghttp2Response
                {
                    Status = 200,
                    Body = body,
                });
                return;
            }

            // Anything else: HTTP/1.1 on the same port, written as plaintext because kTLS is
            // producing the records.
            while (true)
            {
                RecvSnapshot snapshot = await conn.ReadAsync();
                bool sawRequest = false;

                unsafe
                {
                    while (conn.TryGetItem(snapshot, out ioxide.utils.SpscRecvRing.Item item))
                    {
                        if (item.HasBuffer)
                        {
                            sawRequest |= tls.Decrypt(item.Ptr, item.Len).Length > 0;
                            conn.ReturnBuffer(in item);
                        }
                    }
                }

                if (sawRequest)
                {
                    conn.Write(http11Response);
                    await conn.FlushAsync();
                }

                if (snapshot.IsClosed || tls.Closed) return;
                conn.ResetRead();
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[http2-tls] connection failed: {e.Message}");
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

Console.WriteLine($"[http2-tls] {config.ReactorCount} reactors on :{config.Tcp!.Port}, "
                + $"ALPN h2 then http/1.1, cert {certPath}");

foreach (Thread thread in threads)
{
    thread.Join();
}
