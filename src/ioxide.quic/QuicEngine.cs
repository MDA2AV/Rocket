using System.Runtime.InteropServices;

namespace ioxide.quic;

/// <summary>
/// ngtcp2-backed QUIC engine for the ioxide reactor (server side). The engine plugs into the
/// core's QUIC transport through <see cref="ioxide.QuicConnection"/> /
/// <see cref="ioxide.QuicOptions.ConnectionFactory"/>; the protocol glue lands incrementally.
/// Today this package ships the self-contained native bundle and its binding surface.
/// </summary>
public static class QuicEngine
{
    /// <summary>Version of the bundled ngtcp2 - also proves the native bundle loads.</summary>
    public static unsafe string NativeVersion()
    {
        Ngtcp2.ngtcp2_info* info = Ngtcp2.ngtcp2_version(0);
        return info == null ? "unknown" : Marshal.PtrToStringUTF8((nint)info->version_str) ?? "unknown";
    }
}
