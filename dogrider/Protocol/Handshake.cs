using System.Security.Cryptography;
using System.Text;

namespace dogrider.Protocol;

/// <summary>
/// RFC 6455 handshake. The client sends an HTTP/1.1 Upgrade request with a random Sec-WebSocket-Key;
/// the server responds with the SHA-1+Base64 of (key + magic GUID) in Sec-WebSocket-Accept.
/// </summary>
public static class Handshake
{
    private const string MagicGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    public static string CreateAcceptKey(string clientKey)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(clientKey + MagicGuid));
        return Convert.ToBase64String(hash);
    }
}
