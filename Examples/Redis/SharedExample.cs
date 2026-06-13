using System.Buffers;
using System.Text;
using ioxide;
using ioxide.redis;
using ioxide.utils;

namespace Examples.Redis;

/// <summary>
/// Redis analogue of <see cref="Examples.Pg.SharedExample"/>: every request runs a GET through the
/// per-reactor RedisPool. Same shared-buffer-ring carry-slab hot path - only the backend call
/// differs - so it benchmarks the ioxide.redis pipelining path under wrk. Seed the key first:
/// <c>SET ioxide:bench 42</c>.
/// </summary>
public static class SharedExample
{
    private const string BenchKey = "ioxide:bench";

    public static async Task Handle(Reactor r, Connection conn)
    {
        var pool = r.GetService<RedisPool>();

        // Carry for bytes a read leaves behind - the head of a split request.
        var inflight = new byte[16 * 1024];
        int inflightTail = 0;

        while (true)
        {
            var snapshot = await conn.ReadAsync();

            var rings = conn.GetSnapshotMemories(snapshot);
            if (rings.Length > 0)
            {
                ReadOnlySequence<byte> data;
                if (inflightTail == 0 && rings.Length == 1)
                {
                    data = new ReadOnlySequence<byte>(rings[0].Memory);
                }
                else if (inflightTail == 0)
                {
                    data = rings.ToReadOnlySequence();
                }
                else
                {
                    var first = new RingSegment(inflight.AsMemory(0, inflightTail), 0);
                    var last  = first;
                    for (int i = 0; i < rings.Length; i++)
                        last = last.Append(rings[i].Memory, rings[i].BufferId);
                    data = new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
                }

                long consumed = 0;
                bool respond  = false;
                while (Http.TryParseRequest(data.Slice(consumed), out _, out long length))
                {
                    consumed += length;

                    // io_uring send + recv to Redis, on the same ring (pipelined).
                    string? value = await pool.GetAsync(BenchKey);

                    string body = $"redis={value}";
                    conn.Write(Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {body.Length}\r\n\r\n{body}"));
                    respond = true;
                }

                ReadOnlySequence<byte> rest = data.Slice(consumed);
                rest.CopyTo(inflight);
                inflightTail = (int)rest.Length;

                conn.ReturnBuffers(rings);

                if (respond) await conn.FlushAsync();
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
