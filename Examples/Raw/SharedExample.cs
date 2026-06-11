using ioxide;

namespace Examples.Raw;

/// <summary>
/// Raw read and flush, no database: drain the slices, return the buffers, answer a fixed
/// plaintext response. The HTTP-side ceiling of the runtime.
/// </summary>
public static class SharedExample
{
    public static async Task Handle(Reactor r, Connection conn)
    {
        while (true)
        {
            // io_uring recv - resumes inline on the reactor.
            var snapshot = await conn.ReadAsync();

            // Raw read: drain the slices, return each buffer to the ring.
            while (conn.TryGetItem(snapshot, out var item))
            {
                if (item.HasBuffer) conn.ReturnBuffer(in item);
            }

            conn.Write(Http.OkResponse);
            await conn.FlushAsync();   // io_uring send

            if (snapshot.IsClosed)
            {
                conn.DecRef();
                return;
            }

            conn.ResetRead();
        }
    }
}
