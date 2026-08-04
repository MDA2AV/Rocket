# ioxide.redis

Redis client for the [ioxide](https://github.com/MDA2AV/ioxide) io_uring runtime. Pooled
ring-native connections per reactor, full RESP2 protocol, a generic command API plus typed helpers
across every data type, and pipelining - all with inline completion resume.

```csharp
reactor.OnStart = r => RedisPool.Start(r, new RedisOptions
{
    Host = "127.0.0.1", Port = 6379, PoolSize = 8,
});

// in a handler - cache-aside:
var redis = reactor.GetService<RedisPool>();
string? cached = await redis.GetAsync($"item:{id}");
if (cached is null)
{
    cached = await LoadFromDb(id);
    await redis.SetExAsync($"item:{id}", cached, seconds: 1);   // 1s TTL
}
```

## Surface

- **Generic**: `ExecuteAsync(command, args...)` runs *any* Redis command and returns a `RespValue`
  (the full RESP2 taxonomy - null, simple/bulk string, integer, error, nested array).
- **Typed helpers**: strings, keys, hashes, lists, sets, sorted sets, pub/sub, scripting - layered
  over `ExecuteAsync`, so anything not wrapped still works through it.
- **Pipelining**: `PipelineAsync(cmd1, cmd2, ...)` sends a batch and reads all replies in one round
  trip.
- **Pool**: rent for a multi-command sequence (MULTI/EXEC, pipelines) or use the pool's
  `GetAsync` / `SetExAsync` / `DelAsync` / `ExecuteAsync` shortcuts for one-offs.

## Connection

`RedisOptions`: `Host` (IPv4 literal), `Port`, `Password` (+ optional `User` for ACL AUTH),
`Database` (SELECT on connect), `PoolSize` (connections per reactor). One connection carries one
in-flight command or pipeline; total server connections = `PoolSize × ReactorCount`.

Requires `ioxide`. RESP2 (Redis 6+/7). MIT.
