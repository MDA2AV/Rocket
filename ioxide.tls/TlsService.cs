using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using ioxide.utils;

namespace ioxide.tls;

/// <summary>
/// Per-reactor TLS termination. <see cref="AcceptAsync"/> drives the TLS 1.3 handshake through
/// OpenSSL memory BIOs - handshake bytes ride the same ring recv/send as everything else - then
/// enables kernel TLS transmit offload, so the handler keeps writing plaintext through
/// <c>conn.Write</c>/<c>FlushAsync</c> while the kernel produces the records. Receives stay in
/// userspace: the handler passes each ciphertext slice through <see cref="TlsSession.Decrypt"/>.
/// Requests are small and responses are big, so the kernel does the heavy direction.
/// </summary>
public sealed class TlsService
{
    private readonly nint _ctx;

    // ssl* -> captured traffic secret, filled by the keylog callback during the handshake.
    private static readonly ConcurrentDictionary<nint, byte[]> ServerSecrets = new();
    private static readonly byte[] AlpnWire = BuildAlpnWire("http/1.1");

    private TlsService(nint ctx) => _ctx = ctx;

    /// <summary>Create the per-reactor service and register it. Call from <c>Reactor.OnStart</c>.</summary>
    public static TlsService Start(Reactor reactor, TlsOptions options)
    {
        nint ctx = OpenSsl.SSL_CTX_new(OpenSsl.TLS_server_method());
        if (ctx == 0)
        {
            throw new IOException($"SSL_CTX_new: {OpenSsl.LastError()}");
        }

        // TLS 1.3 only, one suite: kTLS needs AES-128-GCM keys and a known layout.
        OpenSsl.SSL_CTX_ctrl(ctx, OpenSsl.SSL_CTRL_SET_MIN_PROTO_VERSION, OpenSsl.TLS1_3_VERSION, 0);
        OpenSsl.SSL_CTX_ctrl(ctx, OpenSsl.SSL_CTRL_SET_MAX_PROTO_VERSION, OpenSsl.TLS1_3_VERSION, 0);
        if (OpenSsl.SSL_CTX_set_ciphersuites(ctx, "TLS_AES_128_GCM_SHA256") != 1)
        {
            throw new IOException($"set_ciphersuites: {OpenSsl.LastError()}");
        }

        // No session tickets: they would consume record sequence numbers after the
        // handshake and break the kTLS handoff (which programs seq = 0).
        OpenSsl.SSL_CTX_set_num_tickets(ctx, 0);

        if (OpenSsl.SSL_CTX_use_certificate_chain_file(ctx, options.CertificatePath) != 1)
        {
            throw new IOException($"certificate '{options.CertificatePath}': {OpenSsl.LastError()}");
        }
        if (OpenSsl.SSL_CTX_use_PrivateKey_file(ctx, options.KeyPath, OpenSsl.SSL_FILETYPE_PEM) != 1)
        {
            throw new IOException($"private key '{options.KeyPath}': {OpenSsl.LastError()}");
        }

        unsafe
        {
            delegate* unmanaged<nint, nint, void> keylog = &KeylogCallback;
            OpenSsl.SSL_CTX_set_keylog_callback(ctx, (nint)keylog);

            delegate* unmanaged<nint, nint, nint, nint, uint, nint, int> alpn = &AlpnSelectCallback;
            OpenSsl.SSL_CTX_set_alpn_select_cb(ctx, (nint)alpn, 0);
        }

        var service = new TlsService(ctx);
        reactor.AddService(service);
        return service;
    }

    /// <summary>
    /// Run the server handshake on an accepted connection. Resumes inline on the reactor like
    /// every other await. Returns the session used to decrypt inbound records; any application
    /// data that arrived alongside the final handshake flight is already in
    /// <see cref="TlsSession.DrainPlaintext"/>.
    /// </summary>
    public async ValueTask<TlsSession> AcceptAsync(Connection conn)
    {
        nint ssl = OpenSsl.SSL_new(_ctx);
        if (ssl == 0)
        {
            throw new IOException($"SSL_new: {OpenSsl.LastError()}");
        }

        nint rbio = OpenSsl.BIO_new(OpenSsl.BIO_s_mem());
        nint wbio = OpenSsl.BIO_new(OpenSsl.BIO_s_mem());
        OpenSsl.SSL_set_bio(ssl, rbio, wbio);   // ssl owns both BIOs now
        OpenSsl.SSL_set_accept_state(ssl);

        var session = new TlsSession(ssl, rbio);

        try
        {
            while (true)
            {
                int ret = OpenSsl.SSL_accept(ssl);

                await FlushOutbound(conn, wbio);   // server flights stage into the slab

                if (ret == 1)
                {
                    break;
                }

                int err = OpenSsl.SSL_get_error(ssl, ret);
                if (err != OpenSsl.SSL_ERROR_WANT_READ)
                {
                    throw new IOException($"TLS handshake failed: {OpenSsl.LastError()}");
                }

                // Need more ciphertext from the client.
                RecvSnapshot snapshot = await conn.ReadAsync();
                bool fed = FeedInbound(conn, rbio, snapshot);
                conn.ResetRead();

                if (snapshot.IsClosed && !fed)
                {
                    throw new IOException("connection closed during TLS handshake");
                }
            }

            // App data that rode in with the client's Finished is sitting in the
            // rbio - decrypt it now so the handler starts with a clean slate.
            session.DrainPending();

            byte[] secret = ServerSecrets.TryRemove(ssl, out byte[]? s)
                ? s
                : throw new IOException("TLS handshake completed but no server traffic secret was captured");

            // Everything the handshake needed to send is flushed; from the next
            // write on, the kernel produces the records. kTLS rejects MSG_WAITALL,
            // so switch this connection's sends to plain (the reactor still loops
            // on short sends).
            Ktls.EnableTx(conn.ClientFd, secret);
            conn.SendOpFlags = 0;

            return session;
        }
        catch
        {
            ServerSecrets.TryRemove(ssl, out _);
            session.Dispose();
            throw;
        }
    }

    private static async ValueTask FlushOutbound(Connection conn, nint wbio)
    {
        int pending = (int)OpenSsl.BIO_ctrl_pending(wbio);
        while (pending > 0)
        {
            int n = StageOutbound(conn, wbio, Math.Min(pending, 8 * 1024));
            if (n <= 0)
            {
                break;
            }
            await conn.FlushAsync();
            pending -= n;
        }
    }

    private static unsafe int StageOutbound(Connection conn, nint wbio, int chunk)
    {
        Span<byte> dst = conn.GetSpan(chunk);
        int n;
        fixed (byte* p = dst)
        {
            n = OpenSsl.BIO_read(wbio, p, chunk);
        }
        if (n > 0)
        {
            conn.Advance(n);
        }
        return n;
    }

    private static unsafe bool FeedInbound(Connection conn, nint rbio, in RecvSnapshot snapshot)
    {
        bool any = false;
        while (conn.TryGetItem(snapshot, out SpscRecvRing.Item item))
        {
            if (item.HasBuffer)
            {
                OpenSsl.BIO_write(rbio, item.Ptr, item.Len);
                conn.ReturnBuffer(in item);
                any = true;
            }
        }
        return any;
    }

    [UnmanagedCallersOnly]
    private static void KeylogCallback(nint ssl, nint line)
    {
        string? text = Marshal.PtrToStringUTF8(line);
        if (text == null)
        {
            return;
        }

        // "SERVER_TRAFFIC_SECRET_0 <client_random_hex> <secret_hex>"
        if (text.StartsWith("SERVER_TRAFFIC_SECRET_0 ", StringComparison.Ordinal))
        {
            int lastSpace = text.LastIndexOf(' ');
            if (lastSpace > 0)
            {
                ServerSecrets[ssl] = Convert.FromHexString(text.AsSpan(lastSpace + 1).TrimEnd());
            }
        }
    }

    [UnmanagedCallersOnly]
    private static unsafe int AlpnSelectCallback(nint ssl, nint outPtr, nint outLen, nint inPtr, uint inLen, nint arg)
    {
        // Find our protocol in the client's length-prefixed offer list and point
        // *out into the client buffer (standard practice - it outlives the callback).
        var offered = new ReadOnlySpan<byte>((void*)inPtr, (int)inLen);
        ReadOnlySpan<byte> want = AlpnWire.AsSpan(1);   // skip our length prefix

        int i = 0;
        while (i < offered.Length)
        {
            int len = offered[i];
            if (len == want.Length && offered.Slice(i + 1, len).SequenceEqual(want))
            {
                *(nint*)outPtr = inPtr + i + 1;
                *(byte*)outLen = (byte)len;
                return OpenSsl.SSL_TLSEXT_ERR_OK;
            }
            i += 1 + len;
        }

        return OpenSsl.SSL_TLSEXT_ERR_NOACK;   // no match: continue without ALPN
    }

    private static byte[] BuildAlpnWire(string protocol)
    {
        var wire = new byte[1 + protocol.Length];
        wire[0] = (byte)protocol.Length;
        for (int i = 0; i < protocol.Length; i++)
        {
            wire[1 + i] = (byte)protocol[i];
        }
        return wire;
    }
}
