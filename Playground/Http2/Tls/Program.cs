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

(string certPath, string keyPath) = QuicCert.Ensure(
    Env.StrOrNull("PLAYGROUND_TLS_CERT"),
    Env.StrOrNull("PLAYGROUND_TLS_KEY"));

var config = new ServerConfig
{
    ReactorCount = Env.Int("PLAYGROUND_REACTORS", Environment.ProcessorCount),
    // PLAYGROUND_INCREMENTAL=1 switches the recv path to per-connection incremental buffer rings.
    Incremental = Env.Flag("PLAYGROUND_INCREMENTAL") ? new IncrementalOptions() : null,
    Tcp = new TcpOptions
    {
        Port = Env.Port("PLAYGROUND_PORT", 8443),
    },
};

int bodyBytes = Env.Int("PLAYGROUND_BODY", 2);
byte[] body = bodyBytes == 2 ? "ok"u8.ToArray() : [.. Enumerable.Repeat((byte)'x', bodyBytes)];

// PLAYGROUND_TLSPIPE picks the inbound TLS pipe: pump (default) | inplace | direct.
string tlsPipe = Env.Str("PLAYGROUND_TLSPIPE", "pump");
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
        // PLAYGROUND_KTLS_RX=1 hands inbound decryption to the kernel as well as outbound.
        KernelRx = Env.Flag("PLAYGROUND_KTLS_RX"),
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
                // THE POINT OF kTLS RX. The kernel already decrypted these bytes, so plaintext is
                // sitting in ring memory - which means the ordinary zero-copy reader can hand it
                // straight out. No pump, no owned Pipe, no decrypt step: a TLS connection uses the
                // exact same TcpConnectionDualPipe a cleartext one does.
                // ...but only when the handshake swallowed nothing. If the client's first bytes
                // rode in with its Finished flight they were decrypted into the session, not into
                // any recv buffer, and a pipe that knows nothing about TLS will never yield them.
                // For h2 that is the connection preface, so it is the common case, not an edge one.
                IDuplexPipe pipe = tls.KernelRx && tls.DrainPlaintext().IsEmpty
                    ? new TcpConnectionDualPipe(conn)
                    : tlsPipe switch
                {
                    "inplace" => new TlsConnectionDualPipeInPlace(conn, tls, ownsSession: false),
                    "direct"  => new TlsConnectionDualPipeDirect(conn, tls, ownsSession: false),
                    _         => new TlsConnectionDualPipe(conn, tls, ownsSession: false),
                };
                // Only the TLS pipes own anything; the plain one is a view over the connection.
                await using IAsyncDisposable owner =
                    pipe as IAsyncDisposable ?? NullAsyncDisposable.Instance;

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
                    conn.Write(http11Response);
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
                + $"inbound={tlsPipe}");

foreach (Thread thread in threads)
{
    thread.Join();
}

// IDuplexPipe is not disposable, and TcpConnectionDualPipe genuinely has nothing to release - it
// is a pair of views over the connection. This keeps the one `await using` above honest.
internal sealed class NullAsyncDisposable : IAsyncDisposable
{
    public static readonly NullAsyncDisposable Instance = new();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
