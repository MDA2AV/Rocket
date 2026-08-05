using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: InternalsVisibleTo("Ioxide.Tests.Http")]
[assembly: InternalsVisibleTo("Ioxide.Tests.Unit")]

namespace ioxide.httpclient;

/// <summary>
/// P/Invoke surface of the ioxide.nghttp2 shim - nghttp2 statically linked into one self-contained
/// native library (scripts/build-nghttp2-native.sh). "ih2" = ioxide http2.
///
/// nghttp2 is sans-I/O: it never touches a socket. Bytes go in through <see cref="ih2_read"/> and
/// come out through <see cref="ih2_write"/>, so the reactor keeps ownership of the loop and the
/// connection - which is what lets an HTTP/2 response resume its awaiting handler inline.
///
/// Every entry point is reactor-thread-only, and callbacks fire INSIDE <see cref="ih2_read"/>:
/// they may only deposit state, never re-enter the session.
/// </summary>
internal static unsafe partial class Nghttp2
{
    private const string Lib = "ioxide_nghttp2";

    /// <summary>Callback table handed to <see cref="ih2_client_new"/>. Each entry is an
    /// [UnmanagedCallersOnly] static; `user` is a GCHandle to the managed bridge.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Callbacks
    {
        public delegate* unmanaged<void*, int, void> OnBeginHeaders;
        public delegate* unmanaged<void*, int, byte*, nuint, byte*, nuint, void> OnHeader;
        public delegate* unmanaged<void*, int, void> OnEndHeaders;
        public delegate* unmanaged<void*, int, byte*, nuint, void> OnData;
        public delegate* unmanaged<void*, int, void> OnEndStream;
        public delegate* unmanaged<void*, int, uint, void> OnStreamError;
    }

    /// <summary>Create a client session and queue the connection preface + SETTINGS. Returns the
    /// native handle, or 0 on allocation/init failure.</summary>
    [DllImport(Lib)] internal static extern nint ih2_client_new(Callbacks callbacks, void* user);

    /// <summary>Submit a request. Headers are packed [u16 namelen][name][u16 valuelen][value]...,
    /// pseudo-headers first. The body is copied natively and freed on stream close. Returns the
    /// stream id, or a negative nghttp2 error.</summary>
    [DllImport(Lib)] internal static extern int ih2_submit_request(nint connection,
        byte* headers, nuint headersLen, byte* body, nuint bodyLen);

    /// <summary>Feed received bytes; response callbacks fire from inside this call. Returns bytes
    /// consumed, or a negative nghttp2 error.</summary>
    [DllImport(Lib)] internal static extern nint ih2_read(nint connection, byte* data, nuint dataLen);

    /// <summary>Drain pending egress into <paramref name="buffer"/>. Returns bytes written
    /// (0 = nothing pending), or a negative nghttp2 error.</summary>
    [DllImport(Lib)] internal static extern nint ih2_write(nint connection, byte* buffer, nuint bufferLen);

    /// <summary>Non-zero once the session wants neither reads nor writes - the connection is spent.</summary>
    [DllImport(Lib)] internal static extern int ih2_is_dead(nint connection);

    /// <summary>Free the session and any retained request bodies.</summary>
    [DllImport(Lib)] internal static extern void ih2_free(nint connection);

    [DllImport(Lib)] private static extern byte* ih2_strerror(int errorCode);

    internal static string StrError(int errorCode)
    {
        byte* text = ih2_strerror(errorCode);
        return text == null ? $"nghttp2 error {errorCode}" : Marshal.PtrToStringUTF8((nint)text) ?? $"{errorCode}";
    }
}
