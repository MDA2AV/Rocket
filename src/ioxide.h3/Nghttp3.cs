using System.Runtime.InteropServices;

namespace ioxide.h3;

/// <summary>
/// P/Invoke surface of the bundled native library: the ioxide.h3 shim (native/ioxide_h3_shim.c)
/// over nghttp3, linked into one shared library (scripts/build-h3-native.sh) with no external
/// dependencies. The shim owns every nghttp3 struct layout in C, so this surface stays small and
/// stable.
/// </summary>
internal static unsafe class Nghttp3
{
    // Resolves runtimes/linux-x64/native/libioxide_h3.so from the package (or beside the app for
    // ProjectReference builds).
    internal const string Lib = "ioxide_h3";

    // Mirrors ih3_callbacks in the shim: request events delivered on the reactor thread. Blittable
    // function pointers (UnmanagedCallersOnly statics) - no per-call marshalling.
    [StructLayout(LayoutKind.Sequential)]
    internal struct Callbacks
    {
        public delegate* unmanaged<void*, long, void>                          OnBeginHeaders;
        public delegate* unmanaged<void*, long, byte*, nuint, byte*, nuint, void> OnHeader;
        public delegate* unmanaged<void*, long, int, void>                     OnEndHeaders;
        public delegate* unmanaged<void*, long, byte*, nuint, void>            OnData;
        public delegate* unmanaged<void*, long, void>                          OnEndStream;
    }

    [DllImport(Lib)] internal static extern nint ih3_server_new(Callbacks cbs, void* user);
    [DllImport(Lib)] internal static extern void ih3_free(nint conn);
    [DllImport(Lib)] internal static extern int  ih3_bind_streams(nint conn, long ctrl, long qenc, long qdec);
    [DllImport(Lib)] internal static extern long ih3_read_stream(nint conn, long streamId, byte* data, nuint dataLen, int fin);
    [DllImport(Lib)] internal static extern int  ih3_submit_response(nint conn, long streamId, byte* headers, nuint headersLen, byte* body, nuint bodyLen);
    [DllImport(Lib)] internal static extern long ih3_writev(nint conn, long* streamId, int* fin, byte* buf, nuint bufLen);
    [DllImport(Lib)] internal static extern int  ih3_close_stream(nint conn, long streamId, ulong appError);
    [DllImport(Lib)] internal static extern nint ih3_strerror(int liberr);
    [DllImport(Lib)] internal static extern nint ih3_version();

    internal static string StrError(int liberr) => Marshal.PtrToStringUTF8(ih3_strerror(liberr)) ?? liberr.ToString();
    internal static string Version() => Marshal.PtrToStringUTF8(ih3_version()) ?? "unknown";
}
