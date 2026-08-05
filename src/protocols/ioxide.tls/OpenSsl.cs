using System.Runtime.InteropServices;

namespace ioxide.tls;

/// <summary>
/// Minimal OpenSSL 3 surface: server context, memory-BIO handshake, keylog, and the client side of
/// the same (connect state, ALPN offer, peer verification).
/// </summary>
internal static unsafe partial class OpenSsl
{
    private const string Ssl = "libssl.so.3";
    private const string Crypto = "libcrypto.so.3";

    public const int SSL_FILETYPE_PEM = 1;
    public const int SSL_ERROR_NONE = 0;
    public const int SSL_ERROR_SSL = 1;
    public const int SSL_ERROR_WANT_READ = 2;
    public const int SSL_ERROR_WANT_WRITE = 3;
    public const int SSL_ERROR_SYSCALL = 5;
    public const int SSL_ERROR_ZERO_RETURN = 6;
    public const int SSL_CTRL_SET_TLSEXT_HOSTNAME = 55;
    public const int SSL_CTRL_SET_MIN_PROTO_VERSION = 123;
    public const int SSL_CTRL_SET_MAX_PROTO_VERSION = 124;
    public const int TLSEXT_NAMETYPE_host_name = 0;
    public const int TLS1_2_VERSION = 0x0303;
    public const int TLS1_3_VERSION = 0x0304;
    public const int SSL_TLSEXT_ERR_OK = 0;
    public const int SSL_TLSEXT_ERR_NOACK = 3;
    public const int CRYPTO_EX_INDEX_SSL = 0;
    public const int SSL_VERIFY_NONE = 0x00;
    public const int SSL_VERIFY_PEER = 0x01;
    public const long X509_V_OK = 0;

    [LibraryImport(Ssl)] public static partial nint TLS_server_method();
    [LibraryImport(Ssl)] public static partial nint TLS_client_method();
    [LibraryImport(Ssl)] public static partial nint SSL_CTX_new(nint method);
    [LibraryImport(Ssl, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int SSL_CTX_use_certificate_chain_file(nint ctx, string file);
    [LibraryImport(Ssl, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int SSL_CTX_use_PrivateKey_file(nint ctx, string file, int type);
    [LibraryImport(Ssl, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int SSL_CTX_set_ciphersuites(nint ctx, string list);
    [LibraryImport(Ssl)] public static partial long SSL_CTX_ctrl(nint ctx, int cmd, long arg, nint parg);
    [LibraryImport(Ssl)] public static partial int SSL_CTX_set_num_tickets(nint ctx, nuint num);
    [LibraryImport(Ssl)] public static partial void SSL_CTX_set_keylog_callback(nint ctx, nint cb);
    [LibraryImport(Ssl)] public static partial void SSL_CTX_set_alpn_select_cb(nint ctx, nint cb, nint arg);

    [LibraryImport(Ssl)] public static partial void SSL_CTX_free(nint ctx);

    [LibraryImport(Ssl)] public static partial nint SSL_new(nint ctx);
    [LibraryImport(Ssl)] public static partial void SSL_free(nint ssl);
    [LibraryImport(Ssl)] public static partial void SSL_set_accept_state(nint ssl);
    [LibraryImport(Ssl)] public static partial void SSL_set_connect_state(nint ssl);
    [LibraryImport(Ssl)] public static partial void SSL_set_bio(nint ssl, nint rbio, nint wbio);
    [LibraryImport(Ssl)] public static partial int SSL_accept(nint ssl);
    [LibraryImport(Ssl)] public static partial int SSL_connect(nint ssl);
    [LibraryImport(Ssl)] public static partial int SSL_read(nint ssl, byte* buf, int num);
    [LibraryImport(Ssl)] public static partial int SSL_write(nint ssl, byte* buf, int num);
    [LibraryImport(Ssl)] public static partial int SSL_shutdown(nint ssl);
    [LibraryImport(Ssl)] public static partial int SSL_get_error(nint ssl, int ret);
    [LibraryImport(Ssl)] public static partial long SSL_ctrl(nint ssl, int cmd, long larg, nint parg);

    // --- client-side verification and ALPN ------------------------------------------------------

    /// <summary>Trust anchors from the system store (/etc/ssl/certs and friends).</summary>
    [LibraryImport(Ssl)] public static partial int SSL_CTX_set_default_verify_paths(nint ctx);

    [LibraryImport(Ssl, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int SSL_CTX_load_verify_locations(nint ctx, string? caFile, string? caPath);

    [LibraryImport(Ssl)] public static partial void SSL_CTX_set_verify(nint ctx, int mode, nint callback);
    [LibraryImport(Ssl)] public static partial long SSL_get_verify_result(nint ssl);

    /// <summary>
    /// The hostname the peer certificate must match. Distinct from SNI: SNI tells the server which
    /// certificate to send, this checks the one it sent. Both are needed.
    /// </summary>
    [LibraryImport(Ssl, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int SSL_set1_host(nint ssl, string hostname);

    /// <summary>Protocols we offer, as a length-prefixed wire list. Returns 0 on SUCCESS.</summary>
    [LibraryImport(Ssl)] public static partial int SSL_CTX_set_alpn_protos(nint ctx, byte* protos, uint length);

    [LibraryImport(Ssl)] public static partial void SSL_get0_alpn_selected(nint ssl, byte** data, uint* length);

    // Per-SSL user data: associate a managed object (a GCHandle) with an SSL so the static keylog
    // callback can find the right session without a global, pointer-keyed map.
    [LibraryImport(Ssl)] public static partial int SSL_set_ex_data(nint ssl, int idx, nint data);
    [LibraryImport(Ssl)] public static partial nint SSL_get_ex_data(nint ssl, int idx);
    [LibraryImport(Crypto)] public static partial int CRYPTO_get_ex_new_index(int classIndex, long argl, nint argp, nint newFunc, nint dupFunc, nint freeFunc);

    [LibraryImport(Crypto)] public static partial nint BIO_new(nint type);
    [LibraryImport(Crypto)] public static partial int BIO_free(nint bio);
    [LibraryImport(Crypto)] public static partial nint BIO_s_mem();
    [LibraryImport(Crypto)] public static partial int BIO_write(nint bio, byte* data, int dlen);
    [LibraryImport(Crypto)] public static partial int BIO_read(nint bio, byte* data, int dlen);
    [LibraryImport(Crypto)] public static partial nuint BIO_ctrl_pending(nint bio);
    [LibraryImport(Crypto)] public static partial ulong ERR_get_error();
    [LibraryImport(Crypto)] public static partial void ERR_error_string_n(ulong e, byte* buf, nuint len);

    public static string LastError()
    {
        ulong code = ERR_get_error();
        if (code == 0) return "no OpenSSL error";

        Span<byte> buf = stackalloc byte[256];
        string message;
        fixed (byte* p = buf)
        {
            ERR_error_string_n(code, p, (nuint)buf.Length);
            message = Marshal.PtrToStringUTF8((nint)p) ?? code.ToString();
        }

        // Drain any piggy-backed errors so the next LastError() isn't stale.
        while (ERR_get_error() != 0)
        {
        }
        return message;
    }
}
