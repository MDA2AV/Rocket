# RingPg — Postgres over io_uring, hosted on a thread-per-core reactor

A minimal **io_uring-native Postgres driver**. The DB socket's `SEND`/`RECV` ride the *same* io_uring
ring the server accepts HTTP on; a query `await` resumes **inline on the reactor thread**. No `Npgsql`,
no `System.Net.Sockets`, no .NET socket engine, **no thread pool** anywhere on the query path.

It is **host-agnostic**: the library talks to an abstract `IRingHost`, so any io_uring reactor can drive
it. This document describes the library and its integration into **Minima**.

---

## 1. The big idea

A normal .NET Postgres client (Npgsql) does its socket I/O on .NET's socket engine (epoll + the thread
pool). From a custom io_uring reactor that means every DB call leaves the reactor, runs on a pool thread,
and has to be *marshalled back* — a round-trip per `await`.

RingPg removes that entirely. The Postgres TCP connection is **just another fd on the reactor's ring**:

```
        ┌────────────────── one reactor thread, one io_uring ring ──────────────────┐
        │                                                                            │
  client fd ──RECV──►  HTTP handler  ──SEND/RECV on DB fd──►  Postgres               │
        ▲                   │  ▲                                   │                 │
        └───────SEND────────┘  └────────── CQE (inline resume) ◄───┘                 │
        └────────────────────────────────────────────────────────────────────────────┘
```

The query SEND and the row RECV are ordinary SQEs. Their CQEs complete an `IValueTaskSource`
(`RunContinuationsAsynchronously = false`), so the `await` continues **synchronously on the reactor**,
right where it left off. Nothing is ever scheduled to another thread.

---

## 2. Library layout (`zerg/RingPg/`)

| File | Responsibility |
|------|----------------|
| `IRingHost.cs` | **The seam.** `IRingHost` (what a reactor implements) + `IRingCompletion` (what the reactor calls back). |
| `Pg.cs` | Pure Postgres v3 wire protocol — `Startup`, `Query`, `TryParse`. No ring, no reactor. |
| `PgConnection.cs` | A connection: blocking handshake once, then ring-based `QueryAsync`. Owns the IVTS. |
| `Native.cs` | libc P/Invokes for the one-time blocking connect/handshake (`socket`/`connect`/`send`/`recv`/`setsockopt`). |

The library has **no io_uring bindings of its own** — it never touches a ring directly. It only knows how
to *ask a host* to send/recv on an fd. That is the whole point of the decoupling.

---

## 3. The seam: `IRingHost` / `IRingCompletion`

```csharp
// Implemented by ANY io_uring reactor (Minima, loom, rhythm, …)
public interface IRingHost
{
    void Bind(int fd, IRingCompletion target);   // route this fd's CQEs to target
    void SubmitSend(int fd, nint buf, int len);   // IORING_OP_SEND  on fd
    void SubmitRecv(int fd, nint buf, int len);   // IORING_OP_RECV  on fd
}

// Implemented by the client (PgConnection). Called by the reactor, inline, on a matching CQE.
public interface IRingCompletion
{
    void Complete(int result);                    // result = CQE.res (bytes, or <0 error)
}
```

Three calls. The reactor owns the ring (single issuer); the client never sees it. Buffers cross the seam
as **`nint`** (a raw address), *not* `byte*` — see §6 for why that matters.

---

## 4. `PgConnection` — the client

### 4.1 Connection setup (one-time, blocking)

`PgConnection.Connect(host, user, db)`:

1. `socket()` + `connect()` to `127.0.0.1:5432` (blocking).
2. **`setsockopt(TCP_NODELAY)`** — critical, see §6.
3. Send the `StartupMessage`, blocking-`recv` until `ReadyForQuery` ('Z'). Trust auth → no password.
4. Allocate native send/recv buffers, `host.Bind(fd, this)` so future CQEs route here.

The handshake is blocking because it happens **once, at reactor startup**, before serving. Everything
after rides the ring.

### 4.2 The IVTS

```csharp
public sealed class PgConnection : IRingCompletion, IValueTaskSource<int>
{
    private ManualResetValueTaskSourceCore<int> _io = new() { RunContinuationsAsynchronously = false };

    public  void Complete(int result) => _io.SetResult(result);   // reactor calls this on the CQE
    private ValueTask<int> SendOp(int len) { _io.Reset(); var vt = new ValueTask<int>(this, _io.Version);
                                             _host.SubmitSend(Fd, _send, len); return vt; }
    // RecvOp is symmetric with SubmitRecv.
}
```

`RunContinuationsAsynchronously = false` is the load-bearing flag: `SetResult` runs the awaiting
continuation **inline**, on the reactor thread that reaped the CQE.

### 4.3 `QueryAsync`

```csharp
public async Task<string> QueryAsync(string sql)
{
    int len = BuildQuery(sql);          // 'Q' message into the send buffer (unsafe helper)
    await SendOp(len);                  // SEND query  → CQE → resume inline
    _recvLen = 0;
    while (true)
    {
        int n = await RecvOp();         // RECV rows   → CQE → resume inline
        if (n <= 0) return "";
        if (Finish(n, out string r)) return r;   // parse to ReadyForQuery, pull first field
    }
}
```

Note `QueryAsync` is **not** in an `unsafe` context (it can `await`); all pointer work lives in the
`BuildQuery` / `Finish` helpers, and the buffers are held as `nint`.

---

## 5. Integration into Minima

Minima becomes an `IRingHost`. Four small touch-points:

### 5.1 `Reactor/Reactor.Db.cs` — implement the seam

```csharp
public sealed unsafe partial class Reactor : IRingHost
{
    private const ulong KindDb = 5UL << 32;      // CQE user_data tag (joins KindAccept/Recv/Send/Wake)
    private IRingCompletion? _dbTarget;
    public  PgConnection? Db;                     // this reactor's connection

    public void Bind(int fd, IRingCompletion t) => _dbTarget = t;

    public void SubmitSend(int fd, nint buf, int len) { /* GetSqeOrFlush → IORING_OP_SEND, user_data = KindDb|fd */ }
    public void SubmitRecv(int fd, nint buf, int len) { /* GetSqeOrFlush → IORING_OP_RECV, user_data = KindDb|fd */ }
}
```

### 5.2 `Reactor/Reactor.cs` — route the CQE

```csharp
private void Dispatch(in IoUringCqe cqe)
{
    ulong kind = cqe.user_data & 0xffffffff_00000000UL;
    ...
    if (kind == KindDb) { _dbTarget?.Complete(cqe.res); return; }   // ← resumes QueryAsync inline
    ...
}
```

### 5.3 `Reactor/Reactor.cs` — open the connection in `Run()`

```csharp
if (Environment.GetEnvironmentVariable("MINIMA_DB") == "1")
    Db = RingPg.PgConnection.Connect(this, "bench", "bench");
```

One connection per reactor (per-core, shared-nothing).

### 5.4 `Program.cs` — query in the handler

```csharp
RecvSnapshot snap = await conn.ReadAsync();           // Minima IVTS — handles fragmentation (§7)
...
if (reactor.Db != null)
    WriteDbResponse(conn, await reactor.Db.QueryAsync("SELECT 42"));
else
    conn.Write(Program.Response);
await conn.FlushAsync();
```

---

## 6. Gotchas (read these)

- **`TCP_NODELAY` is mandatory.** Without it, small queries hit Nagle + delayed-ACK and stall ~40 ms each
  (~32 req/s). With it, ~58 K req/s @ 20 µs. Set once at connect (`PgConnection.cs`).
- **`nint`, not `byte*`, at the seam.** An `async` method can't live in an `unsafe` context, and a `byte*`
  local can't cross an `await`. `nint` is an ordinary integer — it crosses awaits freely; the pointer work
  is confined to small `unsafe` helpers. This is why `IRingHost` takes `nint`.
- **Trust auth only.** The handshake assumes `POSTGRES_HOST_AUTH_METHOD=trust`. SCRAM/MD5 are not
  implemented (yet).
- **Simple query, text format.** `Q` messages only; the first DataRow's first field is returned as text.
  No Parse/Bind/Execute (prepared statements), no parameters, no binary decoding (yet).

---

## 7. Why Minima (and not loom)

loom (the other io_uring host) drives its handler from the reactor as a per-recv callback — it **cannot
re-`await` for more data** if a request arrives fragmented. Minima's connection exposes
`await conn.ReadAsync()` (its own IVTS over the recv CQEs), so the handler is a real coroutine over the
wire and naturally handles partial reads. RingPg rides Minima's IVTS for the *client* side and Minima's
ring for the *DB* side — both resume inline. The library itself is neutral; loom could host it too, it just
isn't the right front-end for real HTTP.

---

## 8. Concurrency limitation & next steps

**One `PgConnection` per reactor ⇒ queries are serial per reactor** (shared IVTS + buffers). Fine for a
single connection; under concurrent load, two handlers on the same reactor would collide.

The fix belongs **in the library**: a `PgPool` of N `PgConnection`s per reactor + an async acquire (park the
handler when the pool is drained, resume when one frees). Total pool size is bounded by Postgres
`max_connections`, so a ring-native DB server is *connection-pool-bound* — like any async-DB server, but
with zero per-query thread-pool overhead.

Roadmap: `PgPool` · SCRAM auth · extended protocol (prepared statements + parameters) · binary type
decoding · pipelining multiple in-flight queries per connection.

---

## 9. Running it

```bash
# 1. Postgres (trust auth, host network so it's plain 127.0.0.1:5432)
docker run -d --name loom-pg --network host \
  -e POSTGRES_USER=bench -e POSTGRES_DB=bench -e POSTGRES_HOST_AUTH_METHOD=trust \
  postgres:16-alpine

# 2. Minima with the DB path on
MINIMA_DB=1 dotnet run -c Release --project Minima

# 3. Hit it
curl -s http://localhost:8080/        # → db=42
```

Measured: **db=42**, ~**57.9 K req/s @ 20 µs** single-connection, **one PG backend per reactor**, and the
process thread count stays flat — no socket-engine or pool threads spun up.

---

## 10. File map

```
RingPg/
  IRingHost.cs        IRingHost + IRingCompletion           (the seam)
  Pg.cs               Startup / Query / TryParse            (pure wire protocol)
  PgConnection.cs     Connect + QueryAsync + IVTS           (the client)
  Native.cs           libc P/Invokes for the handshake

Minima/
  Reactor/Reactor.Db.cs   implements IRingHost (KindDb)
  Reactor/Reactor.cs      KindDb dispatch + Connect() in Run()
  Program.cs              handler calls reactor.Db.QueryAsync(...)
```
