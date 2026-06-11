using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace ioxide.tls;

/// <summary>
/// Kernel TLS transmit offload: derive the TLS 1.3 record keys from the server traffic secret
/// (HKDF-Expand-Label) and program them into the socket. After this, plain io_uring sends carry
/// plaintext and the kernel produces the records.
/// </summary>
internal static unsafe class Ktls
{
    private const int SOL_TCP = 6;
    private const int TCP_ULP = 31;
    private const int SOL_TLS = 282;
    private const int TLS_TX = 1;
    private const ushort TLS_1_3_VERSION = 0x0304;
    private const ushort TLS_CIPHER_AES_GCM_128 = 51;

    [DllImport("libc", SetLastError = true)]
    private static extern int setsockopt(int fd, int level, int optname, void* optval, uint optlen);

    /// <summary>tls12_crypto_info_aes_gcm_128 - the layout the kernel expects (40 bytes).</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct CryptoInfoAesGcm128
    {
        public ushort Version;
        public ushort CipherType;
        public fixed byte Iv[8];
        public fixed byte Key[16];
        public fixed byte Salt[4];
        public fixed byte RecSeq[8];
    }

    /// <summary>Enable kTLS TX with keys derived from the TLS 1.3 server traffic secret.</summary>
    public static void EnableTx(int fd, byte[] serverTrafficSecret)
    {
        byte[] key = ExpandLabel(serverTrafficSecret, "key", 16);
        byte[] nonce = ExpandLabel(serverTrafficSecret, "iv", 12);

        var info = new CryptoInfoAesGcm128
        {
            Version = TLS_1_3_VERSION,
            CipherType = TLS_CIPHER_AES_GCM_128,
        };
        for (int i = 0; i < 16; i++) info.Key[i] = key[i];
        for (int i = 0; i < 4; i++) info.Salt[i] = nonce[i];      // nonce[0..4]
        for (int i = 0; i < 8; i++) info.Iv[i] = nonce[4 + i];    // nonce[4..12]
        // RecSeq stays 0: session tickets are disabled, so no server record was
        // sent under the application traffic key before the handoff.

        ReadOnlySpan<byte> ulp = "tls"u8;
        fixed (byte* p = ulp)
        {
            if (setsockopt(fd, SOL_TCP, TCP_ULP, p, 3) != 0)
            {
                throw new IOException(
                    $"kTLS: TCP_ULP failed (errno {Marshal.GetLastPInvokeError()}); is the 'tls' kernel module available?");
            }
        }

        if (setsockopt(fd, SOL_TLS, TLS_TX, &info, (uint)sizeof(CryptoInfoAesGcm128)) != 0)
        {
            throw new IOException($"kTLS: TLS_TX failed (errno {Marshal.GetLastPInvokeError()})");
        }
    }

    /// <summary>RFC 8446 HKDF-Expand-Label with an empty context.</summary>
    private static byte[] ExpandLabel(byte[] secret, string label, int length)
    {
        string full = "tls13 " + label;
        var info = new byte[2 + 1 + full.Length + 1];
        info[0] = (byte)(length >> 8);
        info[1] = (byte)length;
        info[2] = (byte)full.Length;
        for (int i = 0; i < full.Length; i++) info[3 + i] = (byte)full[i];
        info[^1] = 0;   // empty context

        return HKDF.Expand(HashAlgorithmName.SHA256, secret, length, info);
    }
}
