using System.Text;
using ioxide;
using ioxide.utils;

namespace Playground.Shared.Http;

/// <summary>
/// Reading the request off the recv ring. Every sample has to drain the ring - the buffers go back
/// to the reactor - and the samples that route also need the request target out of the request line.
/// </summary>
public static class RequestParser
{
    /// <summary>Drain the recv without parsing (the raw-style samples ignore the request).</summary>
    public static void Drain(TcpConnection conn, RecvSnapshot snapshot)
    {
        while (conn.TryGetItem(snapshot, out SpscRecvRing.Item item))
        {
            if (item.HasBuffer)
            {
                conn.ReturnBuffer(in item);
            }
        }
    }

    /// <summary>Drain the recv and return the request target path (defaults to "/").</summary>
    public static string ReadPath(TcpConnection conn, RecvSnapshot snapshot)
    {
        string path = "/";

        while (conn.TryGetItem(snapshot, out SpscRecvRing.Item item))
        {
            if (item.HasBuffer)
            {
                if (TryReadTarget(item.AsSpan(), out ReadOnlySpan<byte> target))
                {
                    path = Encoding.ASCII.GetString(target);
                }

                conn.ReturnBuffer(in item);
            }
        }

        return path;
    }

    /// <summary>
    /// Pull the target out of a request line: "GET /css/app.css?v=1 HTTP/1.1" -> "/css/app.css".
    /// </summary>
    /// <remarks>
    /// The span-based lookup the file sample needs lives there, not here: resolving a target against
    /// the asset cache has to happen while the recv buffer is still valid, so it belongs next to the
    /// drain loop that owns those buffers.
    /// </remarks>
    public static bool TryReadTarget(ReadOnlySpan<byte> request, out ReadOnlySpan<byte> target)
    {
        target = default;

        int firstSpace = request.IndexOf((byte)' ');
        if (firstSpace < 0) return false;

        ReadOnlySpan<byte> afterMethod = request[(firstSpace + 1)..];
        int secondSpace = afterMethod.IndexOf((byte)' ');
        if (secondSpace < 0) return false;

        target = afterMethod[..secondSpace];

        int query = target.IndexOf((byte)'?');
        if (query >= 0) target = target[..query];

        return true;
    }
}
