# rhythm

A synchronous, single-issuer **io_uring** HTTP/1.1 server in C# (NativeAOT) —
the standalone, clean-room implementation of the `minima-sync` design.

The reactor thread is the *sole issuer*: it submits every SQE and processes every
CQE, calling the handler **inline**. No async/`IValueTaskSource`, no thread pool,
no cross-thread queues — and therefore none of the overhead that a thread-per-core
io_uring server pays when a handler leaves its reactor thread.

## Layout

| File | Role |
|---|---|
| `io_uring/Native.cs` | raw io_uring syscalls + struct layouts + socket calls (libc P/Invoke) |
| `io_uring/Ring.cs` | thin ring wrapper (`SINGLE_ISSUER \| DEFER_TASKRUN`): GetSqe / SubmitAndWait / CqReady / CqeAt / CqAdvance |
| `Reactor.cs` | the synchronous event loop: multishot accept, single-shot recv, batched send, fd-indexed connection slots + pool |
| `Connection.cs` | per-connection state: fd + native recv/write buffers (no GC heap) |
| `Http.cs` | the **workload**, decoupled from the reactor — parses requests and serializes responses over spans only |
| `Dataset.cs` | json item model, parsed once at startup; serialized per request |
| `Affinity.cs` | discover allowed CPUs + (optionally) pin a reactor thread |
| `Program.cs` | spawn the reactor threads (default 12, unpinned) |

## Design

- **N reactor threads** (default **12**), each with its own ring + own
  `SO_REUSEPORT` listener (shared-nothing; the kernel shards connections).
  **Unpinned by default**; set `RHYTHM_PIN=1` to pin reactor *i* to the *i*-th
  allowed CPU.
- **Multishot accept; single-shot recv into a per-connection buffer**, parsed in
  place; responses serialized **straight into the send buffer**. recv↔send
  alternate, so at most one is in flight per connection.
- **Zero hot-path allocation** — pooled connections + native (`NativeMemory`)
  buffers. **NativeAOT** — a single native binary, no runtime, no JIT warmup.

## The reactor/handler seam

`Http.Process(recv, write, ds, out wrote, out close)` is the boundary: the
reactor owns io_uring; the handler is pure span-in/span-out and touches nothing
else. That is deliberately the place an **async workload** plugs in — e.g. an
io_uring-native DB call. The right way to stay fast on this architecture is to
route that I/O **through the same ring** (the DB socket's SEND/RECV become SQEs;
their completions come back as CQEs on the reactor thread), turning the handler
into a per-connection **state machine the reactor drives** — fully asynchronous,
yet never leaving the reactor thread. *(Not yet implemented — next step.)*

## Endpoints

| Method | Path | Response |
|---|---|---|
| GET/POST | `/baseline11?a=&b=` | `text/plain` — `a + b` (+ POST body) |
| GET | `/pipeline` | `text/plain` — `ok` |
| GET | `/json/{count}?m=N` | `application/json` — items with `total = price*quantity*N` |

## Build & run

```bash
dotnet run -c Release                         # JIT, for iteration
dotnet publish -c Release -p:PublishAot=true  # NativeAOT binary -> bin/.../publish/rhythm
RHYTHM_DATASET=/path/to/dataset.json ./rhythm
```

io_uring needs a recent kernel; under Docker run with `--security-opt seccomp=unconfined`.
