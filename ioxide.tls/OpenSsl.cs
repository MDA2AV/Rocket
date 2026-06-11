using System.Runtime.InteropServices;

namespace ioxide.tls;

/// <summary>Minimal OpenSSL 3 surface: server context, memory-BIO handshake, keylog.</summary>
internal static unsafe partial class OpenSsl
{
    private const string Ssl = "libssl.so.3";
    private const string Crypto = "libcrypto.so.3";

    public const int SSL_FILETYPE_PEM = 1;
    public const int SSL_ERROR_WANT_READ = 2;
    public const int SSL_ERROR_WANT_WRITE = 3;
    public const int SSL_ERROR_ZERO_RETURN = 6;
    public const int SSL_CTRL_SET_MIN_PROTO_VERSION = 123;
    public const int SSL_CTRL_SET_MAX_PROTO_VERSION = 124;
    public const int TLS1_3_VERSION = 0x0304;
    public const int SSL_TLSEXT_ERR_OK = 0;
    public const int SSL_TLSEXT_ERR_NOACK = 3;

    [LibraryImport(Ssl)] public static partial nint TLS_server_method();
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

    [LibraryImport(Ssl)] public static partial nint SSL_new(nint ctx);
    [LibraryImport(Ssl)] public static partial void SSL_free(nint ssl);
    [LibraryImport(Ssl)] public static partial void SSL_set_accept_state(nint ssl);
    [LibraryImport(Ssl)] public static partial void SSL_set_bio(nint ssl, nint rbio, nint wbio);
    [LibraryImport(Ssl)] public static partial int SSL_accept(nint ssl);
    [LibraryImport(Ssl)] public static partial int SSL_read(nint ssl, byte* buf, int num);
    [LibraryImport(Ssl)] public static partial int SSL_get_error(nint ssl, int ret);

    [LibraryImport(Crypto)] public static partial nint BIO_new(nint type);
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
        fixed (byte* p = buf)
        {
            ERR_error_string_n(code, p, (nuint)buf.Length);
            return Marshal.PtrToStringUTF8((nint)p) ?? code.ToString();
        }
    }
}
