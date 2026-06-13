using ioxide;
using ioxide.tls;
using ioxide.utils;

namespace Examples.Tls;

/// <summary>
/// TLS via ioxide.tls: OpenSSL handshake over the ring, then kernel TLS transmit offload - the
/// response below is written as plaintext and the kernel produces the records. Needs the Linux
/// 'tls' module (modprobe tls) and OpenSSL 3.
/// </summary>
public static class KtlsExample
{
    public static async Task Handle(Reactor r, Connection conn)
    {
        TlsSession? tls = null;
        try
        {
            tls = await r.GetService<TlsService>().AcceptAsync(conn);

            // A request can ride in with the handshake's final flight - answer it first.
            if (tls.DrainPlaintext().Length > 0)
            {
                conn.Write(Body.Response);
                await conn.FlushAsync();
            }

            while (true)
            {
                RecvSnapshot snap = await conn.ReadAsync();
                int got = 0;
                while (conn.TryGetItem(snap, out SpscRecvRing.Item item))
                {
                    if (item.HasBuffer)
                    {
                        got += DecryptLen(tls, in item);
                        conn.ReturnBuffer(in item);
                    }
                }

                if (got > 0)
                {
                    conn.Write(Body.Response);   // plaintext in; kernel encrypts on send
                    await conn.FlushAsync();
                }

                if (snap.IsClosed || tls.Closed)
                {
                    return;
                }
                conn.ResetRead();
            }
        }
        catch (Exception e)
        {
            // The reactor invokes handlers fire-and-forget, so a thrown handshake/handler error would
            // otherwise vanish silently (this is how a missing 'tls' kernel module manifested earlier).
            Console.Error.WriteLine($"[tls] connection handler failed: {e.Message}");
        }
        finally
        {
            tls?.Dispose();
            conn.DecRef();
        }
    }

    private static unsafe int DecryptLen(TlsSession tls, in SpscRecvRing.Item item) =>
        tls.Decrypt(item.Ptr, item.Len).Length;
}
