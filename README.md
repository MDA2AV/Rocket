# ioxide

[![ioxide](https://img.shields.io/nuget/v/ioxide?label=ioxide)](https://www.nuget.org/packages/ioxide/)
[![ioxide.pg](https://img.shields.io/nuget/v/ioxide.pg?label=ioxide.pg)](https://www.nuget.org/packages/ioxide.pg/)
[![ioxide.file](https://img.shields.io/nuget/v/ioxide.file?label=ioxide.file)](https://www.nuget.org/packages/ioxide.file/)
[![ioxide.tls](https://img.shields.io/nuget/v/ioxide.tls?label=ioxide.tls)](https://www.nuget.org/packages/ioxide.tls/)
[![ioxide.redis](https://img.shields.io/nuget/v/ioxide.redis?label=ioxide.redis)](https://www.nuget.org/packages/ioxide.redis/)

**A shared-nothing io_uring runtime for .NET.**

One ring per reactor thread - run one per core. HTTP, Postgres, and file I/O submit on that
ring and resume inline on the same thread. No thread pool on the hot path. No native dependencies - raw syscalls, nothing else.

> Linux 6.1+ · .NET 10 · status `0.0.3` - experimental

**[Documentation →](https://mda2av.github.io/ioxide/)** - architecture, guides, the full picture

## Quick start

```bash
dotnet run -c Release --project Playground                     # GET / → ok

PLAYGROUND_MODE=pg   dotnet run -c Release --project Playground  # SELECT 42 over the ring
PLAYGROUND_MODE=file dotnet run -c Release --project Playground  # static files off the ring
```

## How it works

```csharp
var reactor = new Reactor(id, new ServerConfig { Port = 8080 });

// Clients opened here ride this reactor's ring.
reactor.OnStart = r => PgPool.Start(r, pgOptions);

reactor.Handle = async (r, conn) =>
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
            // TryParseRequest, Request, and SqlFor are YOUR code - ioxide
            // hands you raw bytes and stays out of HTTP.
            long consumed = 0;
            bool respond  = false;
            while (TryParseRequest(data.Slice(consumed), out Request request, out long length))
            {
                consumed += length;

                // io_uring send + recv to Postgres, on the same ring.
                var rows = await pool.QueryAsync(SqlFor(request.Path));

                // ioxide doesn't speak HTTP for you - you write the bytes.
                string body = $"db={rows.Value}";
                conn.Write(Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {body.Length}\r\n\r\n{body}"));
                respond = true;
            }

            // Whatever wasn't consumed (a partial request, or everything when
            // nothing completed) moves to the front of the carry - only then
            // do the buffers go back to the ring.
            ReadOnlySequence<byte> rest = data.Slice(consumed);
            rest.CopyTo(inflight);
            inflightTail = (int)rest.Length;

            conn.ReturnBuffers(rings);

            if (respond) await conn.FlushAsync();   // io_uring send, once per batch
        }

        if (snapshot.IsClosed)
        {
            conn.DecRef();
            return;
        }

        conn.ResetRead();
    }
};

// One reactor per core.
new Thread(reactor.Run).Start();
```

Every `await` above is a CQE on this core's ring. Nothing hops threads.

## Projects

```
ioxide/        the engine - reactor, connection, IRingHost seam, client kit
ioxide.pg/     Postgres over the ring: pooled connections, ring-native connect
ioxide.file/   files over the ring: baked asset snapshots + positional reads
Playground/    runnable host (raw · pg · file · hop modes)
Research/      the experimental engines this was distilled from (not referenced)
```

## License

MIT
