using System.Runtime.InteropServices;

namespace ioxide.quic;

/// <summary>
/// P/Invoke surface of the bundled native engine: ngtcp2 with its picotls crypto backend, linked
/// into one shared library (scripts/build-quic-native.sh) whose only external dependency is
/// libcrypto.so.3. The binding grows with the engine work; today it carries the version probe.
/// </summary>
internal static unsafe class Ngtcp2
{
    // Resolves runtimes/linux-x64/native/libioxide_quic.so from the package (or beside the app
    // for ProjectReference builds).
    internal const string Lib = "ioxide_quic";

    [StructLayout(LayoutKind.Sequential)]
    internal struct ngtcp2_info
    {
        public int   age;
        public int   version_num;
        public byte* version_str;
    }

    [DllImport(Lib)]
    internal static extern ngtcp2_info* ngtcp2_version(int leastVersion);
}
