using System.Text;
using ioxide;

namespace Examples.Raw;

/// <summary>
/// Large fixed plaintext body, to exercise the response send path with big payloads - in particular
/// the zero-copy send strategy (ServerConfig.ZeroCopySend). The body is a deterministic byte pattern
/// so a client can checksum it and compare plain SEND vs SEND_ZC output byte-for-byte. Under
/// keep-alive this also exercises slab reuse: the handler only writes the next response after
/// FlushAsync completes, which for a ZC send is the F_NOTIF (buffer-release) - so a correct
/// implementation never recycles the slab while the kernel still owns it.
/// </summary>
public static class BigExample
{
    public const int BodySize = 100 * 1024;   // 100 KB - well past the small-payload regime

    // Built once: header + deterministic body (byte i = i % 251; 251 is prime, so the pattern isn't
    // 256-aligned and a one-byte slip would show up in the checksum).
    private static readonly byte[] Response = BuildResponse();

    private static byte[] BuildResponse()
    {
        byte[] head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: {BodySize}\r\n\r\n");

        byte[] response = new byte[head.Length + BodySize];
        head.CopyTo(response, 0);
        for (int i = 0; i < BodySize; i++)
        {
            response[head.Length + i] = (byte)(i % 251);
        }
        return response;
    }

    public static async Task Handle(Reactor r, TcpConnection conn)
    {
        while (true)
        {
            var snapshot = await conn.ReadAsync();

            while (conn.TryGetItem(snapshot, out var item))
            {
                if (item.HasBuffer) conn.ReturnBuffer(in item);
            }

            conn.Write(Response);
            await conn.FlushAsync();   // ZC: returns on the F_NOTIF, so the slab is free before the next Write

            if (snapshot.IsClosed)
            {
                conn.DecRef();
                return;
            }

            conn.ResetRead();
        }
    }
}
