# ioxide.tls

TLS for the [ioxide](https://github.com/MDA2AV/ioxide) io_uring runtime, both directions:
terminating it as a **server** via kernel TLS (kTLS), and speaking it as a **client** to reach an
`https://` origin. Either way the handshake runs through OpenSSL over the reactor's ring and
resumes inline on the reactor thread.

## Server: terminate TLS

The negotiated keys are handed to the kernel, which then encrypts everything the server sends.
Handlers keep writing plaintext through the normal `conn.Write`/`FlushAsync` API - the encryption
is invisible to them.

```csharp
reactor.OnStart = r => TlsService.Start(r, new TlsOptions
{
    CertificatePath = "/certs/server.crt",
    KeyPath = "/certs/server.key",
});

reactor.Handle = async (r, conn) =>
{
    TlsSession? tls = null;
    if (conn.ListenerPort == 8443)
        tls = await r.GetService<TlsService>().AcceptAsync(conn);   // handshake + kTLS handoff

    // The first request can arrive with the handshake's final flight:
    if (tls != null) Feed(tls.DrainPlaintext());

    // ... send-first loop: respond to buffered requests before parking on a read ...
    // outbound is plaintext (kernel encrypts); inbound decrypts via tls.Decrypt(ptr, len)
};
```

Pair with `ServerConfig.ExtraPorts` to terminate TLS on a second port while plaintext stays on
the first.

## How it works

- **Transmit** is offloaded to the kernel (kTLS TX): after the handshake the reactor's ordinary
  io_uring sends carry plaintext and the kernel produces TLS records - no per-write crypto in
  userspace, the heavy direction for an HTTP server.
- **Receive** stays in userspace: inbound records decrypt through OpenSSL
  (`TlsSession.Decrypt`). Requests are small, so this is cheap.
- **The handshake** rides the ring like any other I/O: OpenSSL drives memory BIOs, the service
  pumps ciphertext through the connection's recv/send. Resumes inline on the reactor.

## Client: reach an https:// origin

`TlsClientContext` is the mirror of `TlsService`. It deliberately does **not** use kTLS: the
offload is a server-side trick that pays off for big responses, whereas a client sends small
requests and reads whatever it is given, so both directions stay in userspace. That also means the
client half needs no kernel module.

```csharp
using var tls = TlsClientContext.Create(new TlsClientOptions
{
    ServerName    = "api.example.com",       // SNI, and the name the certificate must match
    AlpnProtocols = ["h2", "http/1.1"],
});

// ioxide.httpclient takes the context directly:
HttpClientPool.Start(reactor, new HttpClientOptions
{
    Host = "10.0.0.7", Port = 443, Tls = tls,
});
```

Defaults are the safe ones: the chain is verified against the system trust store and the
certificate must match `ServerName`. A private CA sets `CaFile` rather than turning verification
off; `VerifyCertificate = false` exists for self-signed origins in tests and gives you encryption
without authentication, which anything in the path can exploit.

Over TLS, HTTP/2 is selected by ALPN and nothing else - an `Http2ClientPool` with a `Tls` context
refuses a connection whose origin did not choose `h2`, because sending the HTTP/2 preface to a
server expecting HTTP/1.1 hangs instead of failing.

## Requirements

- **Linux kTLS** for the server half only - the `tls` kernel module (`modprobe tls`; standard on
  mainstream distros). The client half runs anywhere.
- **OpenSSL 3** - present in the .NET runtime container images.
- TLS 1.3 with `TLS_AES_128_GCM_SHA256`; ALPN selectable (default `http/1.1`); session tickets
  disabled (they would desync the kTLS record sequence).

Requires `ioxide`. MIT.
