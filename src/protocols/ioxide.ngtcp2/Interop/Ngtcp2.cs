using System.Runtime.InteropServices;

namespace ioxide.ngtcp2;

/// <summary>
/// P/Invoke surface of the bundled native engine: the ioxide.ngtcp2 shim (native/ioxide_ngtcp2_shim.c)
/// over ngtcp2 + its picotls crypto backend, linked into one shared library
/// (scripts/build-ngtcp2-native.sh) whose only external dependency is libcrypto.so.3. The shim owns
/// every ngtcp2/picotls struct layout in C, so this surface stays small and stable.
/// </summary>
internal static unsafe class Ngtcp2
{
    // Resolves runtimes/linux-x64/native/libioxide_ngtcp2.so from the package (or beside the app for
    // ProjectReference builds).
    internal const string Lib = "ioxide_ngtcp2";

    // Mirrors iq_callbacks in the shim: engine events delivered on the reactor thread. Blittable
    // function pointers (UnmanagedCallersOnly statics) - no per-call marshalling.
    [StructLayout(LayoutKind.Sequential)]
    internal struct Callbacks
    {
        public delegate* unmanaged<void*, long, byte*, nuint, int, void> OnStreamData;
        public delegate* unmanaged<void*, long, ulong, void>            OnStreamClose;
        public delegate* unmanaged<void*, void>                         OnHandshakeCompleted;
        public delegate* unmanaged<void*, byte*, nuint, void>           OnNewCid;
        public delegate* unmanaged<void*, byte*, nuint, void>           OnRetireCid;
        public delegate* unmanaged<void*, long, ulong, void>            OnStreamReset;
        public delegate* unmanaged<void*, long, ulong, void>            OnStreamStopSending;
        public delegate* unmanaged<void*, long, ulong, ulong, void>     OnAckedStreamData;
    }

    [DllImport(Lib)] internal static extern nint iq_engine_new(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string certPemPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string keyPemPath,
        nuint cidLen, byte* alpn, nuint alpnLen, Callbacks cbs);

    [DllImport(Lib)] internal static extern void iq_engine_free(nint engine);

    [DllImport(Lib)] internal static extern nint iq_accept(
        nint engine,
        void* localSa, nuint localSaLen,
        void* remoteSa, nuint remoteSaLen,
        byte* pkt, nuint pktLen,
        ulong ts, void* user, byte* scidOut);

    [DllImport(Lib)] internal static extern void iq_conn_free(nint conn);

    [DllImport(Lib)] internal static extern int iq_conn_read(
        nint conn, void* remoteSa, nuint remoteSaLen,
        byte* pkt, nuint pktLen, byte ecn, ulong ts);

    [DllImport(Lib)] internal static extern nint iq_conn_write(
        nint conn, byte* dest, nuint destLen,
        long streamId, byte* data, nuint dataLen, int fin,
        long* pConsumed, ulong ts);

    [DllImport(Lib)] internal static extern nint iq_conn_close(
        nint conn, ulong appErrorCode, byte* dest, nuint destLen, ulong ts);
    [DllImport(Lib)] internal static extern nint iq_conn_write_close(
        nint conn, byte* dest, nuint destLen, ulong ts);

    [DllImport(Lib)] internal static extern ulong iq_conn_expiry(nint conn);
    [DllImport(Lib)] internal static extern int   iq_conn_handle_expiry(nint conn, ulong ts);
    [DllImport(Lib)] internal static extern int   iq_conn_is_established(nint conn);
    [DllImport(Lib)] internal static extern int   iq_conn_in_draining(nint conn);
    [DllImport(Lib)] internal static extern long  iq_conn_open_uni(nint conn);
    [DllImport(Lib)] internal static extern void  iq_conn_set_stream_paced(nint conn, long streamId, int on);
    [DllImport(Lib)] internal static extern void  iq_conn_consume(nint conn, long streamId, ulong n);
    [DllImport(Lib)] internal static extern nuint iq_conn_get_alpn(nint conn, byte* buf, nuint bufLen);

    // --- client side (ioxide.httpclient / QuicClientEngine) ---------------------------------

    [DllImport(Lib)] internal static extern nint iq_client_engine_new(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string alpn, Callbacks callbacks);
    [DllImport(Lib)] internal static extern void iq_client_engine_free(nint engine);

    /// <summary>Open a client connection. scidLen must equal the demux slice of whoever routes our
    /// inbound datagrams (the reactor's QuicOptions.LocalCidLength); scidOut receives the CID to
    /// register for that routing.</summary>
    [DllImport(Lib)] internal static extern nint iq_client_connect(
        nint engine, byte* localSockaddr, nuint localLen, byte* remoteSockaddr, nuint remoteLen,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string serverName, [MarshalAs(UnmanagedType.LPUTF8Str)] string alpn,
        nuint scidLen, ulong ts, void* user, byte* scidOut);

    [DllImport(Lib)] internal static extern long iq_client_open_bidi(nint conn);

    [DllImport(Lib)] internal static extern nint iq_version();
    [DllImport(Lib)] internal static extern nint iq_strerror(int liberr);

    // ngtcp2 error codes the write/read loops branch on (include/ngtcp2/ngtcp2.h).
    internal const int NGTCP2_ERR_STREAM_DATA_BLOCKED   = -208;
    internal const int NGTCP2_ERR_STREAM_SHUT_WR        = -219;
    internal const int NGTCP2_ERR_STREAM_NOT_FOUND      = -220;
    internal const int NGTCP2_ERR_DRAINING              = -224;
    internal const int NGTCP2_ERR_IDLE_CLOSE            = -238;

    internal static string StrError(int liberr) => Marshal.PtrToStringUTF8(iq_strerror(liberr)) ?? liberr.ToString();
}
