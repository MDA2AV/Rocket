# bench

The regression suite: every workload the repo can serve or drive, one number each, so a change
can be checked against the previous run on the same machine.

```bash
dotnet build -c Release ioxide.slnx     # once
bash bench/run.sh
```

Servers run **4 reactors** (`BENCH_REACTORS` overrides); `tcp-raw`/`tcp-pipe` repeat at
**12 reactors** as the io_uring baseline (driven harder: `-t12 -c128`, so the load generator
is not the ceiling). `BENCH_SECONDS` sets the measurement window (default 8).

| workload | server | load |
| --- | --- | --- |
| tcp-raw, tcp-pipe (4r + 12r) | `Playground.Tcp.Raw` / `.Pipe`, 1024 B body | `wrk -t4 -c64` |
| tls-sslstream, tls-ktls | `Playground.Tls.*`, 8 KB body | `wrk` over TLS |
| h3-server | `Playground.Nghttp3` | `h3x -t4 -c64 -m8` |
| client-h1 / h2 / h3 | `Bench.Clients` (4 reactors) against Tcp.Raw / nginx-h2c / Nghttp3 | itself |
| redis, pg, file | `Playground.Redis` / `.Pg` / `.File` | `wrk -t4 -c64` |

Dependencies: `wrk` (required), [`h3x`](https://github.com/MDA2AV) for the h3 server row
(`H3X=/path/to/h3x`), docker for redis / pg / the h2c upstream, the `tls` kernel module for
`tls-ktls`. Anything missing **skips with a note** instead of failing the run.

Numbers are hardware-specific. Compare runs on one machine; never commit them as truth.
