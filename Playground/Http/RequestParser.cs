using System.Text;
using ioxide;
using ioxide.file;
using ioxide.utils;

namespace Playground.Http;

/// <summary>
/// Reading the request off the recv ring. Every mode has to drain the ring - the buffers go back
/// to the reactor - and the modes that route also need the request target out of the request line.
/// </summary>
internal static class RequestParser
{
    /// <summary>Drain the recv without parsing (the raw-style handlers ignore the request).</summary>
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
    /// Drain the recv, resolving the request target against the asset cache while the bytes are
    /// still valid. The lookup is span-based - no string - so it has to happen before the buffer
    /// goes back to the ring.
    /// </summary>
    public static bool TryFindAsset(
        TcpConnection conn,
        RecvSnapshot snapshot,
        StaticAssets.Lease lease,
        out AssetCache.Asset asset)
    {
        bool found = false;
        asset = default;

        while (conn.TryGetItem(snapshot, out SpscRecvRing.Item item))
        {
            if (item.HasBuffer)
            {
                if (!found && TryReadTarget(item.AsSpan(), out ReadOnlySpan<byte> target))
                {
                    found = lease.TryGet(target, out asset);
                }

                conn.ReturnBuffer(in item);
            }
        }

        return found;
    }

    /// <summary>
    /// Pull the target out of a request line: "GET /css/app.css?v=1 HTTP/1.1" -> "/css/app.css".
    /// </summary>
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
