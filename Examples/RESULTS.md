# Benchmark results

One run per mode against this machine. Numbers are hardware-specific - rerun on yours:

```bash
dotnet build -c Release
./Examples/bin/Release/net10.0/Examples <mode> &
wrk -c512 -t18 -d5s http://127.0.0.1:8080/
```

**Environment:** 32-core Linux 6.17, .NET 10, loopback. wrk and the server share the machine.
12 reactors per mode. pg category: local Postgres, trust auth, `SELECT 42` per request through
per-reactor pools of 4 (48 connections total).

## raw - read and flush, no database

The HTTP-side ceiling of the runtime.

| mode | req/s | avg latency | errors |
|---|---|---|---|
| raw-shared      | 3,512,700 | 155 us | 0 |
| raw-incremental | 3,519,821 | 151 us | 0 |
| raw-pipes       | 3,418,231 | 151 us | 0 |

## pg - a real query per request

| mode | req/s | avg latency | errors |
|---|---|---|---|
| pg-shared      | 698,464 | 795 us | 0 |
| pg-incremental | 699,258 | 790 us | 0 |
| pg-pipes       | 706,593 | 745 us | 0 |

## Pool-size sweep (pg-shared)

Is the pg category bottlenecked by connection count? No - doubling past 48 changes nothing:

| pool/reactor | total conns | req/s |
|---|---|---|
| 2 | 24 | 659,841 |
| 4 | 48 | 690,875 |
| 8 | 96 | 687,104 |

(Server max_connections = 100, so 96 is the ceiling at 12 reactors. Override with
`EXAMPLES_PG_POOL`.)

A mid-load CPU snapshot shows where the time actually goes: the ioxide server ~10.4 cores,
wrk ~5 cores, Postgres ~7 cores spread across 48 backends at only 27-36% each, ~20% of the
machine in softirq moving ~3M loopback socket ops/s, 14% idle. No individual Postgres backend
is saturated and neither is the pool - the ceiling is the co-located machine: server, load
generator, and database all sharing 32 cores. On separate hosts the pg numbers would rise
until Postgres itself becomes the wall.

## tls - a medium response, two ways

A fixed 8 KB body over TLS 1.3 (`EXAMPLES_TLS_BODY` to resize), self-signed cert generated at
startup. `wrk -c512 -t18`, keep-alive (handshake amortized). Both face the same wrk client-TLS
load, so the ratio is a fair server-side comparison; absolutes are co-located-limited.

| mode | req/s | TLS out | vs ktls |
|---|---|---|---|
| tls-ktls      | 1,492,775 | 11.7 GB/s | - |
| tls-sslstream |   975,216 |  7.7 GB/s | 0.65x |

- **kTLS (`ioxide.tls`)** offloads response encryption to the kernel on the io_uring send path -
  zero-copy, no managed crypto. Needs Linux's `tls` module (`modprobe tls`) and OpenSSL 3.
- **SslStream over `ConnectionStream`** is fully managed and portable (TLS 1.2 + 1.3, client
  certs, no native dependency), at ~0.65x the throughput. Needs no ioxide changes - it rides the
  `ConnectionStream` bridge plus host glue.
- kTLS is ~1.5x faster, stable across payload sizes (same ratio at a 2-byte body).

## Notes

- **Raw vs pg:** the ~5x gap is the cost of a database round-trip per request plus machine
  sharing, not an ioxide limit - the HTTP side has ~2.8M req/s of headroom.
- **Pipes vs raw API:** ~2.7% behind on the plaintext workload, indistinguishable on the pg
  workload (within run-to-run noise). The adapters are allocation-free at steady state; the
  residual cost is sequence bookkeeping.
- **Shared vs incremental:** identical on this workload (one small request per buffer either
  way). Incremental's advantage shows with many connections sending small fragmented messages,
  not in a uniform benchmark.
- Zero non-2xx and zero socket errors in every run.
