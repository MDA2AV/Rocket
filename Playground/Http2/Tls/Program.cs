using System.IO.Pipelines;
using System.Runtime.InteropServices;
using System.Text;
using ioxide;
using ioxide.nghttp2;
using ioxide.tls;
using Playground.Shared;

// ─────────────────────────────────────────────────────────────────────────────────────────────
//  http2-tls - HTTP/2 over TLS, negotiated by ALPN, alongside HTTP/1.1 on the SAME port. This is
//  what a browser expects: it offers "h2,http/1.1" and the server chooses.
//
//      dotnet run -c Release --project Playground/Http2/Tls
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

// ── Knobs ────────────────────────────────────────────────────────────────────────────────────
// Edit these. That is the whole mechanism - there is no config file and nothing else to find.
// Env.Override exists only so bench/run.sh can drive the sample from outside; delete that line
// when you copy this out and the literals above it are the entire configuration.

ushort port      = 8443;                        // https://127.0.0.1:8443/
int    reactors  = Environment.ProcessorCount;  // one ring per reactor, one reactor per core
int    bodyBytes = 2;                           // "ok" - this sample is about ALPN, not throughput

Env.Override(ref port, ref reactors, ref bodyBytes);

// A real PEM pair, or null to generate a self-signed localhost cert on first run.
string? certOverride = null;
string? keyOverride  = null;

// Per-connection incremental buffer rings instead of the shared ring (kernel 6.12+). The h2 code
// is identical either way; this only changes how recv buffers are handed out.
bool incrementalBuffers = false;

// Hand INBOUND decryption to the kernel as well as outbound. Experimental and off for a reason:
// a TLS 1.3 KeyUpdate cannot be read through IORING_OP_RECV, and roughly one first connection in
// twelve fails outright. See docs/learn/tls.html.
bool kernelRx = false;
// ─────────────────────────────────────────────────────────────────────────────────────────────

(string certPath, string keyPath) = QuicCert.Ensure(certOverride, keyOverride);

var config = new ServerConfig
{
    ReactorCount = reactors,
    Incremental = incrementalBuffers ? new IncrementalOptions() : null,
    Tcp = new TcpOptions
    {
        Port = port,
    },
};

byte[] body = bodyBytes == 2 ? "ok"u8.ToArray() : [.. Enumerable.Repeat((byte)'x', bodyBytes)];

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
        KernelRx = kernelRx,
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
                // The decrypt lives in the pipe, so the HTTP/2 code below is identical to the
                // cleartext sample. Two implementations of the same seam:
                //
                //   default   TlsConnectionDualPipe          decrypts into a Pipe it owns, fed by
                //                                            a pump task
                //   INPLACE=1 TlsConnectionDualPipeInPlace   decrypts inside the recv buffer and
                //                                            hands that memory out - no pump, no
                //                                            Pipe, backpressure is the ring
                //
                // Both are handed to the same Nghttp2Connection, which is the point.
                // The two share only IDuplexPipe, which is not disposable - hence the second
                // declaration rather than one `await using`.
                // One type, both worlds. Which halves it uses is decided by what the handshake
                // achieved, not by anything chosen here - see TlsConnectionDualPipe.
                var pipe = new TlsConnectionDualPipe(conn, tls, ownsSession: false);

                await using var owner = pipe;

                await new Nghttp2Connection(pipe).RunBufferedAsync(_ => new Nghttp2Response
                {
                    Status = 200,
                    Body = body,
                });
                return;
            }

            // Anything else: HTTP/1.1 on the same port, written as plaintext because kTLS is
            // producing the records.
            //
            // The carry is not incidental. TLS hands back RECORDS, not requests, so a request
            // split across two records decrypts twice - and answering on "plaintext arrived"
            // would answer twice to one request. Framing is ours; ioxide does not parse HTTP.
            var carry = new List<byte>();

            // The client's first request usually rides in with its Finished flight, so the
            // handshake already decrypted it and it is sitting in the session, not in any recv
            // buffer. Miss this and that request is dropped and the loop parks on bytes that
            // already arrived - which is exactly what happened here before.
            carry.AddRange(tls.DrainPlaintext());

            while (true)
            {
                bool wrote = false;
                int end;
                while ((end = CollectionsMarshal.AsSpan(carry).IndexOf("\r\n\r\n"u8)) >= 0)
                {
                    carry.RemoveRange(0, end + 4);
                    // Correct whichever backend the session ended up with.
                    tls.Write(conn, http11Response);
                    wrote = true;
                }

                if (wrote)
                {
                    await conn.FlushAsync();
                }

                RecvSnapshot snapshot = await conn.ReadAsync();

                unsafe
                {
                    while (conn.TryGetItem(snapshot, out ioxide.utils.SpscRecvRing.Item item))
                    {
                        if (item.HasBuffer)
                        {
                            carry.AddRange(tls.Decrypt(item.Ptr, item.Len));
                            conn.ReturnBuffer(in item);
                        }
                    }
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
                + $"ALPN h2 then http/1.1, cert {certPath}, "
                + $"rx={(kernelRx ? "kernel" : "openssl")}, tx=kernel");

foreach (Thread thread in threads)
{
    thread.Join();
}

