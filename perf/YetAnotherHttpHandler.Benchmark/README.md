# YetAnotherHttpHandler vs SocketsHttpHandler benchmarks

Compares `Cysharp.Net.Http.YetAnotherHttpHandler` against .NET's built-in HTTP client over the same
in-process Kestrel server, using [BenchmarkDotNet](https://benchmarkdotnet.org/).

Every arm is an `HttpClient`; only the handler underneath differs:

| Arm | Handler | Why it's here |
| --- | --- | --- |
| `HttpClient (default)` | `HttpClientHandler` | **The baseline.** This is what plain `new HttpClient()` gives you — the out-of-the-box .NET client. |
| `SocketsHttpHandler` | `SocketsHttpHandler` | The lower-level .NET stack that `HttpClientHandler` wraps. Included so wrapper overhead can't be mistaken for a difference in the native stack. |
| `YetAnotherHttpHandler` | this library | The thing under test. |

The **Ratio** column is the number to read: `1.00` means parity with the default .NET client,
`< 1.00` means faster, `> 1.00` slower.

The primary configuration is **HTTP/2 over TLS** — it is listed first in every table and is what this
library targets. HTTP/1.1-over-TLS and HTTP/2 cleartext are also measured for contrast.

## Running

The native library must be built for the **same configuration** as the benchmark. Benchmarks require
Release, so build the Release native first:

```bash
.devcontainer/build-native.sh --release          # or: cargo build --release --target <host triple>
dotnet run -c Release --project perf/YetAnotherHttpHandler.Benchmark -- --filter '*'
```

Useful invocations:

```bash
# Just the headline per-request overhead comparison
dotnet run -c Release --project perf/YetAnotherHttpHandler.Benchmark -- --filter '*SmallResponse*'

# Everything except the (deliberately slow) cold-start benchmark, with fewer iterations
dotnet run -c Release --project perf/YetAnotherHttpHandler.Benchmark -- \
  --filter '*SmallResponseBenchmark*' '*DownloadBenchmark*' '*UploadBenchmark*' '*ConcurrencyBenchmark*' \
  --job short

# List what exists without running anything
dotnet run -c Release --project perf/YetAnotherHttpHandler.Benchmark -- --list flat
```

`--job short` trades confidence for wall-clock time; use the default job for numbers you intend to
quote.

## What each benchmark measures

Every benchmark is parameterised by `Transport`: `Http2Tls` (HTTP/2 over TLS via ALPN — the primary
case), `Http1Tls` (HTTP/1.1 over TLS) and `Http2Cleartext` (h2c, prior knowledge). Cleartext h2 is
included to separate protocol cost from TLS cost.

| Benchmark | Operation | What it tells you |
| --- | --- | --- |
| `SmallResponseBenchmark` | `GET` a 6-byte body over a warm connection | Fixed per-request overhead. At this size the measurement is almost entirely handler bookkeeping, including the managed/native boundary. **This is the headline comparison.** |
| `DownloadBenchmark` | Stream a 64 KiB / 1 MiB / 8 MiB response, drained into one reused buffer | Response throughput and flow-control behaviour. |
| `UploadBenchmark` | `POST` 1 KiB / 1 MiB / 8 MiB; server drains and acks | Request-body throughput. |
| `ConcurrencyBenchmark` | 8 or 64 concurrent requests | HTTP/2 stream multiplexing, or HTTP/1.1 connection pooling. |
| `ConnectionSetupBenchmark` | Construct handler → connect (incl. TLS handshake) → one request → dispose | Cold-start cost. The handlers differ most here: YetAnotherHttpHandler builds a native client and rustls config. |

All three handlers are configured as equivalently as the APIs allow:

- Certificate verification is disabled on all of them (the server uses a self-signed cert), so none
  pays for chain building the others skip. This is the only reason `HttpClientHandler` is constructed
  explicitly rather than via bare `new HttpClient()`.
- The protocol version is pinned explicitly — `Http2Only` for YetAnotherHttpHandler,
  `DefaultRequestVersion` + `HttpVersionPolicy.RequestVersionExact` for the two .NET handlers.
- `GlobalSetup` asserts each handler actually negotiated the expected HTTP version, so a silent
  fallback to HTTP/1.1 fails the run instead of producing a meaningless comparison.

## Reading the results — caveats that matter

**`Allocated` understates YetAnotherHttpHandler.** `MemoryDiagnoser` only sees managed heap
allocations. YetAnotherHttpHandler does much of its work in Rust, so its buffers and per-request state
are largely invisible to this column. Managed-allocation ratios here are *not* a statement about total
memory use. The `Mean` column is unaffected.

**`ConcurrencyBenchmark` times are per batch, not per request.** One operation issues `Concurrency`
requests, and BenchmarkDotNet's `OperationsPerInvoke` cannot vary with a parameter. The Ratio is still
valid, since both handlers run identical batches.

**Localhost is not a network.** With no bandwidth limit, no packet loss and sub-millisecond RTT, this
setup emphasises CPU cost per request and de-emphasises everything YetAnotherHttpHandler's HTTP/2
implementation might do better on a real link (flow control, head-of-line behaviour, congestion
response). Treat these as a lower bound on the value of the native stack, not the whole picture.

**The server is in the same process.** Client and server contend for the same cores, so absolute
numbers are pessimistic for both handlers. Comparisons between the two remain fair.

**Both handlers see the same server.** The Kestrel endpoints write from pre-allocated buffers and do
no per-request allocation, deliberately unlike `HttpClientTestServer`, to keep server-side GC out of
the measurement.

## Why not `HttpClientTestServer`?

The test suite's server is a full-featured app whose project also runs `protoc` for its gRPC
endpoints. Depending on it would drag that build into the benchmark for no benefit — the benchmarks
need four trivial endpoints, not the test surface. `BenchmarkServer` reuses only the test server's
self-signed `localhost.pfx`.
