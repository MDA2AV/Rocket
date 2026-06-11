using System.Buffers;
using System.Text;
using ioxide;
using ioxide.pg;
using ioxide.utils;

namespace Examples.Pg;

/// <summary>
/// The landing page's "shared buffer ring" tab, runnable: the raw API with a handler-owned
/// carry slab - the hot path parses straight over the ring memory, only the unconsumed tail
/// is copied. Every request runs a query through the per-reactor PgPool.
/// </summary>
public static class SharedExample
{
    public static async Task Handle(Reactor r, Connection conn)
    {
        var pool = r.GetService<PgPool>();

        // Carry for bytes a read leaves behind - the head of a split request.
        var inflight = new byte[16 * 1024];
        int inflightTail = 0;

        while (true)
        {
            // io_uring recv - resumes inline on the reactor.
            var snapshot = await conn.ReadAsync();

            var rings = conn.GetSnapshotMemories(snapshot);
            if (rings.Length > 0)
            {
                ReadOnlySequence<byte> data;
                if (inflightTail == 0 && rings.Length == 1)
                {
                    // Hot path: one ring, no carry - a single zero-copy segment.
                    data = new ReadOnlySequence<byte>(rings[0].Memory);
                }
                else if (inflightTail == 0)
                {
                    // Several rings, no carry - chain them, still zero-copy.
                    data = rings.ToReadOnlySequence();
                }
                else
                {
                    // Cold path: the carry goes first so a split request reads whole.
                    var first = new RingSegment(inflight.AsMemory(0, inflightTail), 0);
                    var last  = first;
                    for (int i = 0; i < rings.Length; i++)
                        last = last.Append(rings[i].Memory, rings[i].BufferId);
                    data = new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
                }

                // Walk every complete request; stop at the first partial one.
                long consumed = 0;
                bool respond  = false;
                while (Http.TryParseRequest(data.Slice(consumed), out Request request, out long length))
                {
                    consumed += length;

                    var rows = await pool.QueryAsync(Http.SqlFor(request.Path));

                    string body = $"db={rows.Value}";
                    conn.Write(Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {body.Length}\r\n\r\n{body}"));
                    respond = true;
                }

                // Whatever wasn't consumed moves to the front of the carry - only
                // then do the buffers go back to the ring.
                ReadOnlySequence<byte> rest = data.Slice(consumed);
                rest.CopyTo(inflight);
                inflightTail = (int)rest.Length;

                conn.ReturnBuffers(rings);

                if (respond) await conn.FlushAsync();   // once per batch
            }

            if (snapshot.IsClosed)
            {
                conn.DecRef();
                return;
            }

            conn.ResetRead();
        }
    }
}
