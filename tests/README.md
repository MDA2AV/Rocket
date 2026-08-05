# tests

One project per area, each a plain executable with its own entry point, so a suite can be run on
its own and a failure names the area immediately.

```bash
dotnet build -c Release ioxide.slnx
for s in Unit E2E Http Pg Redis Tls File; do
  dotnet run --project tests/Ioxide.Tests.$s -c Release --no-build
done
```

| Suite | Covers | Needs |
| --- | --- | --- |
| `Unit` | The pure logic: QUIC demux packet parse, Alt-Svc negotiation parser, shared HTTP message types | nothing |
| `E2E` | Reactor lifecycle, TCP read/write paths, hardening, UDP, QUIC transport, ngtcp2 engine, both HTTP/3 layers | nothing |
| `Http` | The HTTP client: h1, h2c, h3 and the negotiating layer | h2c tests need an HTTP/2 server |
| `Pg` | The Postgres driver | a postgres |
| `Redis` | The Redis client | a redis |
| `Tls` | kTLS handshake and encrypted writes | the `tls` kernel module (`sudo modprobe tls`) |
| `File` | Static assets: cache, ring reads, revalidation | nothing |

Anything missing **skips** rather than fails, so every suite is safe to run anywhere.

`Ioxide.Tests.Harness` is the shared library: the runner and its asserts, `TestServer` (starts a
real reactor on a unique port), `Client` (a socket-level HTTP client), and the generic handlers.
Module-specific handlers live with their suite - `PgHandlers` in the Pg project, not here - so the
harness never references a module it does not need.

**Style**: these assert behavior, never timings or throughput. A test that would fail on a slow
machine is a bug in the test.
