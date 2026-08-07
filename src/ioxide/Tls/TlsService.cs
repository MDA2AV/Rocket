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
    private readonly GCHandle _alpnHandle;   // roots the ALPN wire bytes the select callback reads via its arg
    private readonly bool _kernelRx;

    // A per-SSL ex_data slot holds a GCHandle to the TlsSession, so the static keylog callback can
    // find the right session for an SSL without a process-global, recycled-pointer-keyed map.
    private static readonly int SslSessionIndex =
        OpenSsl.CRYPTO_get_ex_new_index(OpenSsl.CRYPTO_EX_INDEX_SSL, 0, 0, 0, 0, 0);

    private TlsService(nint ctx, GCHandle alpnHandle, bool kernelRx)
    {
        _ctx = ctx;
        _alpnHandle = alpnHandle;
        _kernelRx = kernelRx;
    }

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

        // The ALPN protocol to select is handed to the (static) callback via its arg, so the
        // configured TlsOptions.Alpn is honored instead of being hard-coded.
        byte[] alpnWire = BuildAlpnWire(options.Alpn);
        GCHandle alpnHandle = GCHandle.Alloc(alpnWire);

        unsafe
        {
            delegate* unmanaged<nint, nint, void> keylog = &KeylogCallback;
            OpenSsl.SSL_CTX_set_keylog_callback(ctx, (nint)keylog);

            delegate* unmanaged<nint, nint, nint, nint, uint, nint, int> alpn = &AlpnSelectCallback;
            OpenSsl.SSL_CTX_set_alpn_select_cb(ctx, (nint)alpn, GCHandle.ToIntPtr(alpnHandle));
        }

        var service = new TlsService(ctx, alpnHandle, options.KernelRx);
        reactor.AddService(service);
        return service;
    }

    /// <summary>
    /// Run the server handshake on an accepted connection. Resumes inline on the reactor like
    /// every other await. Returns the session used to decrypt inbound records; any application
    /// data that arrived alongside the final handshake flight is already in
    /// <see cref="TlsSession.DrainPlaintext"/>.
    /// </summary>
    public async ValueTask<TlsSession> AcceptAsync(TcpConnection conn)
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

        // Associate the session with this SSL so the keylog callback writes the secret onto it
        // directly (no global map). The handle is freed in TlsSession.Dispose.
        GCHandle handle = GCHandle.Alloc(session);
        OpenSsl.SSL_set_ex_data(ssl, SslSessionIndex, GCHandle.ToIntPtr(handle));
        session.AttachHandle(handle);

        try
        {
            while (true)
            {
                int ret = OpenSsl.SSL_accept(ssl);
                int err = ret == 1 ? 0 : OpenSsl.SSL_get_error(ssl, ret);   // read immediately, before other OpenSSL calls

                await FlushOutbound(conn, wbio);   // server flights stage into the slab

                if (ret == 1)
                {
                    break;
                }
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

            // Available only once the handshake is complete, and the handler needs it before it
            // decides which protocol loop to run.
            session.CaptureAlpn();

            // Count BEFORE draining: these are the records the handshake pulled off the socket, so
            // they are invisible to the kernel and the RX sequence number has to skip past them.
            int consumedRecords = session.CountPendingRecords(out bool partialRecord);

            // App data that rode in with the client's Finished is sitting in the
            // rbio - decrypt it now so the handler starts with a clean slate.
            session.DrainPending();

            byte[] secret = session.ServerSecret
                ?? throw new IOException("TLS handshake completed but no server traffic secret was captured");

            // Everything the handshake needed to send is flushed; from the next write on, the kernel
            // produces the records. kTLS rejects MSG_WAITALL, so switch this connection's sends to
            // plain (the reactor still loops on short sends). EnableTx zeros 'secret' once programmed.
            Ktls.EnableTx(conn.ClientFd, secret);
            conn.SendOpFlags = 0;
            session.MarkTxEnabled(conn.ClientFd);

            // RX is opt-in and per connection. A partial record left in the BIO means bytes the
            // kernel will never see, and no sequence number recovers those - that connection stays
            // on the userspace path rather than corrupting itself.
            if (_kernelRx && !partialRecord && session.ClientSecret is not null)
            {
                Ktls.EnableRx(conn.ClientFd, session.ClientSecret, (ulong)consumedRecords);
                session.ClientSecret = null;   // EnableRx zeroed it
                session.MarkKernelRx();
            }

            return session;
        }
        catch
        {
            session.Dispose();   // frees the ex_data GCHandle and the SSL (+ BIOs)
            throw;
        }
    }

    private static async ValueTask FlushOutbound(TcpConnection conn, nint wbio)
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

    private static unsafe int StageOutbound(TcpConnection conn, nint wbio, int chunk)
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

    private static unsafe bool FeedInbound(TcpConnection conn, nint rbio, in RecvSnapshot snapshot)
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

        bool server = text.StartsWith("SERVER_TRAFFIC_SECRET_0 ", StringComparison.Ordinal);
        bool client = text.StartsWith("CLIENT_TRAFFIC_SECRET_0 ", StringComparison.Ordinal);
        if (!server && !client)
        {
            return;
        }

        // Resolve the session from the SSL's ex_data (set in AcceptAsync) - no global pointer map.
        nint data = OpenSsl.SSL_get_ex_data(ssl, SslSessionIndex);
        if (data == 0 || GCHandle.FromIntPtr(data).Target is not TlsSession session)
        {
            return;
        }

        // "<SIDE>_TRAFFIC_SECRET_0 <client_random_hex> <secret_hex>"
        int lastSpace = text.LastIndexOf(' ');
        if (lastSpace <= 0)
        {
            return;
        }

        byte[] secret = Convert.FromHexString(text.AsSpan(lastSpace + 1).TrimEnd());
        if (server)
        {
            session.ServerSecret = secret;
        }
        else
        {
            session.ClientSecret = secret;   // the RX half; only used when TlsOptions.KernelRx is on
        }
    }

    [UnmanagedCallersOnly]
    private static unsafe int AlpnSelectCallback(nint ssl, nint outPtr, nint outLen, nint inPtr, uint inLen, nint arg)
    {
        if (arg == 0 || GCHandle.FromIntPtr(arg).Target is not byte[] wire)
        {
            return OpenSsl.SSL_TLSEXT_ERR_NOACK;
        }

        // SERVER preference: walk OUR list in order and take the first the client also offered, so
        // the order in TlsOptions.Alpn is what decides. Walking the client's list instead would
        // hand it the choice, which is not what an operator listing ["h2", "http/1.1"] means.
        //
        // *out points into the CLIENT's buffer rather than ours - standard practice, since that
        // buffer outlives the callback and ours would have to be pinned to match.
        var offered = new ReadOnlySpan<byte>((void*)inPtr, (int)inLen);

        int ours = 0;
        while (ours < wire.Length)
        {
            int wantLength = wire[ours];
            ReadOnlySpan<byte> want = wire.AsSpan(ours + 1, wantLength);

            int theirs = 0;
            while (theirs < offered.Length)
            {
                int haveLength = offered[theirs];
                if (theirs + 1 + haveLength > offered.Length)
                {
                    break;   // malformed offer list; stop rather than read past it
                }

                if (haveLength == wantLength && offered.Slice(theirs + 1, haveLength).SequenceEqual(want))
                {
                    *(nint*)outPtr = inPtr + theirs + 1;
                    *(byte*)outLen = (byte)haveLength;
                    return OpenSsl.SSL_TLSEXT_ERR_OK;
                }
                theirs += 1 + haveLength;
            }

            ours += 1 + wantLength;
        }

        // Nothing in common. NOACK continues without the extension rather than failing the
        // handshake - the client may still speak HTTP/1.1 quite happily.
        return OpenSsl.SSL_TLSEXT_ERR_NOACK;
    }

    /// <summary>
    /// Protocols as ALPN wants them: each one a length byte then its ASCII name, in our preference
    /// order. The select callback walks this, so position here is what decides the negotiation.
    /// </summary>
    private static byte[] BuildAlpnWire(string[] protocols)
    {
        if (protocols.Length == 0)
        {
            throw new ArgumentException("At least one ALPN protocol is required.", nameof(protocols));
        }

        int total = 0;
        foreach (string protocol in protocols)
        {
            if (protocol.Length is 0 or > 255)
            {
                throw new ArgumentException($"ALPN protocol '{protocol}' must be 1..255 bytes.", nameof(protocols));
            }
            total += 1 + protocol.Length;
        }

        var wire = new byte[total];
        int cursor = 0;
        foreach (string protocol in protocols)
        {
            wire[cursor++] = (byte)protocol.Length;
            for (int i = 0; i < protocol.Length; i++)
            {
                wire[cursor++] = (byte)protocol[i];
            }
        }
        return wire;
    }
}
