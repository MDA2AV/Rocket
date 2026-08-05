# TLS

TLS termination for [ioxide](https://github.com/MDA2AV/ioxide), via **kernel TLS (kTLS)**. Part of
the `ioxide` package - there is nothing extra to install, and none of it loads unless you call it. The handshake runs through OpenSSL over the reactor's ring; the negotiated keys are
handed to the kernel, which then encrypts everything the server sends. Handlers keep writing
plaintext through the normal `conn.Write`/`FlushAsync` API - the encryption is invisible to them.

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

## Requirements

- **Linux kTLS** - the `tls` kernel module (`modprobe tls`; standard on mainstream distros).
- **OpenSSL 3** - present in the .NET runtime container images.
- TLS 1.3 with `TLS_AES_128_GCM_SHA256`; ALPN selectable (default `http/1.1`); session tickets
  disabled (they would desync the kTLS record sequence).

Ships in `ioxide`. MIT.
