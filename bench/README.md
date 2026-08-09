# bench

The Playground samples are the regression fixtures. They are not demo code that happens to be
runnable - every knob is a literal you edit AND an environment variable a harness can drive, which
is what lets one build be measured in every configuration without rebuilding.

```bash
dotnet build -c Release ioxide.slnx     # once

bash bench/any.sh                       # every registered sample
bash bench/any.sh Tcp/Raw Tls/OpenSsl   # just these
bash bench/any.sh --list                # what is registered, and what is runnable here
```

## any.sh - the suite

`bench/samples.tsv` registers every sample: protocol, port, request path, the origin to start
first, and extra environment. It is asserted complete against the Playground tree, so a sample
nobody can benchmark shows up as a gap rather than as silence.

`any.sh` reads that table and needs no per-sample code. For each row it starts an origin when the
fixture proxies or calls out, waits until the sample actually answers, warms it, measures, tears
it down, and waits for the port to be released before the next row.

It records **throughput and CPU microseconds per request**. The second one is the point: on
loopback throughput saturates and stops separating implementations, while CPU per request keeps
going. A cell is **discarded rather than reported** when

- the reactors were not saturated (below `MIN_UTIL`% of `REACTORS` cores) - an idle reactor polls
  an empty ring, that time lands in `utime`, and CPU per request comes out inflated,
- the load generator saw non-2xx responses or socket errors,
- the sample served a different byte count than the row asks for,
- more than one process is listening on the port. Under `SO_REUSEPORT` a leaked server from an
  earlier run will happily bind alongside the new one and take half the load, which has produced
  phantom numbers here before.

A number from a broken fixture is worse than no number.

| variable | default | |
| --- | --- | --- |
| `REACTORS` | 2 | keep it small, so the load generator is never the ceiling |
| `SECONDS_` | 10 | measured seconds per sample |
| `CONNS` | 64 | connections |
| `THREADS` | 8 | load generator threads |
| `MIN_UTIL` | 90 | reject a cell whose reactors were not saturated |
| `BASELINE` | `results/latest.json` | compare against this instead of the previous run |
| `H3X` | `~/h3x/build/h3x` | the HTTP/3 driver |

## Results are kept

Every run writes `bench/results/<utc>.json` - commit, host, kernel, settings and one row per
sample - and updates `latest.json`. Each sample prints against its previous value:

```
Tcp/Raw          889608   2.30us   -3.3%
Clients/File     681472   3.00us   +1.2%
```

A number with nothing to compare against cannot catch a regression, which is the whole point.

**Only diff runs taken at the same settings.** Every result file records `reactors`, `conns`,
`threads` and `seconds`, because a single operating point can rank two implementations either way
- see the HTTP/2 note below.

## Load generators

| protocol | driver |
| --- | --- |
| `h1`, `h1s` | `wrk` |
| `h2c`, `h2` | `h2load` (nghttp2) |
| `h3` | [`h3x`](https://github.com/MDA2AV) - the `h2load` in most distributions is built without ngtcp2/nghttp3 and cannot drive HTTP/3 (it reports 0 started) |
| `echo` | `Playground/Clients/Quic` - the QUIC echo servers speak no HTTP, so nothing off the shelf can drive them. The driver is itself a sample, not a bench-only artifact |

`Bench.Clients` drives ioxide's own HTTP clients (`h1`/`h2`/`h3`) against an external server, for
the rows where the thing under test is our client rather than our server.

Anything missing **skips with a note** instead of failing the run.

## The focused matrices

These answer one question each, holding everything else constant, and exist because the suite
deliberately does not.

- **`tls-matrix.sh`** - h1 and h2 across plaintext / OpenSSL / kTLS-TX / kTLS-both, at 64 B and
  8 KB. There is no kTLS-RX-only cell: RX is programmed at the same handoff as TX, so asking for
  it alone silently gets you OpenSSL.
- **`file-matrix.sh`** - static files, reading straight into the write slab versus through a
  buffer, crossed with cleartext / kTLS / OpenSSL. Whether the slab path is legal at all follows
  from what the slab must hold when it is sent, so there is no OpenSSL+slab cell. Runs **one
  saturated reactor**: an earlier four-reactor version reported an 11% win where the
  single-reactor method measures 22%.
- **`h2-matrix.sh`** - nghttp2 versus the pure-C# HTTP/2, with and without TLS, on one load
  generator, interleaved so drift hits both arms equally.

## A caution the HTTP/2 matrix earned

Measured here at 2 reactors, 64 connections, 32 streams, a 2-byte body, the pure-C# server runs
2.7x nghttp2 cleartext and 2.5x over TLS, at about a third of the CPU per request. TLS costs
**both** the same 0.05us per request - identical absolute cost from the same OpenSSL - so it only
looks like a bigger hit on the faster server.

That ordering narrows as reactors rise and connections-per-reactor falls (2.76x at 2 reactors,
1.48x at 16), and a 2-byte body measures framing and HPACK rather than moving data, so it should
not be read as a general claim.

The reason this section exists: a previously documented run of the same comparison used
`h2load -n 300000`, which **completes in 190 milliseconds** - connection setup and ramp, never
steady state - and reported the two as equal. Prefer `-D` over `-n`, and check the reported
duration before believing a throughput number.

Numbers are hardware-specific. Compare runs on one machine; never commit them as truth.
