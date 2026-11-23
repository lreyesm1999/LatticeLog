# Rust SDK: producer cost and throughput — v1 (P21)

Date: 2026-11-22 · `cargo run --release --example bench -- <folder> 500000` (`sdks/rust/examples/bench.rs`) · Intel
Core i9-14900KF, Windows 11 10.0.26300, Rust 1.100.0 (`x86_64-pc-windows-gnu`, release, thin LTO, one codegen unit),
process affinity 0xFF (8 logical CPUs, because of the machine's known instability under all-core load). Files on a
**7,200 rpm SATA HDD** (Seagate ST1000DM010), system antivirus active. **One machine, one run per row**: the numbers do
not generalize.

## Method

- Every scenario writes the same logical record to a file: message + 3 attributes (int, float, string), plus a thread
  attribute (SDK builder) or a span (the `tracing` scenarios).
- **call**: time spent in the calling thread per record, averaged per thread. **end to end**: until everything is handed
  to the OS (SDK: `flush(false)`; baseline: dropping the worker guard). No fsync is waited for in either.
- Baseline: `tracing_subscriber::fmt().json()` through `tracing_appender::non_blocking` in lossless mode (it blocks
  instead of dropping, like the SDK's default `Saturation::Block`).
- When the producers are faster than the writer, the queue fills up and `Block` makes **call ≈ the writer's cost per
  record**: the multi-thread rows measure saturation, not the cost of one call. The latency line measures one call at a
  rate the writer keeps up with.

## 1. Results

| scenario | threads | records | call ns/record | end to end records/s | bytes/record |
|---|---:|---:|---:|---:|---:|
| latticelog builder (Balanced) | 1 | 500,000 | 216 | 4,619,945 | 203 |
| latticelog `tracing` layer | 1 | 500,000 | 321 | 3,076,238 | 219 |
| fmt json + tracing-appender | 1 | 500,000 | 1,618 | 617,856 | 224 |
| latticelog builder (Balanced) | 4 | 2,000,000 | 976 | 3,483,620 | 203 |
| latticelog `tracing` layer | 4 | 2,000,000 | 2,238 | 1,780,043 | 219 |
| fmt json + tracing-appender | 4 | 2,000,000 | 5,549 | 676,273 | 224 |
| latticelog builder (Balanced) | 8 | 4,000,000 | 2,676 | 2,915,882 | 204 |
| latticelog `tracing` layer | 8 | 4,000,000 | 4,150 | 1,917,273 | 220 |
| fmt json + tracing-appender | 8 | 4,000,000 | — | — | — (not valid: its output file was empty at the end, twice; cause not investigated) |
| latticelog builder (Fast) | 1 | 500,000 | 203 | 4,874,444 | 203 |
| latticelog builder (Durable) | 1 | 500,000 | 3,448 | 203,357 | 203 |

- Single-thread call latency, 200,000 calls (includes the clock read, ~100 ns resolution on Windows): p50 200 ns,
  p99 400 ns, p99.9 4.5 µs, max 1.5 ms.
- Disabled level (`debug!` with an `Info` minimum): 0.18 ns per call (one relaxed atomic load; arguments are not
  evaluated).
- `Durable` fsyncs every batch on the HDD (each fsync takes milliseconds), so one producer that waits on a full queue
  gets ~200k records/s. That is the cost of the guarantee, not of the encoding.
- Above 1 thread the end-to-end rate falls (4.6 M → 2.9 M records/s at 8 threads): the producers share one batch
  lock, and the single writer already moves ~0.9 GB/s of lines (where the limit lies was not profiled further).
  Sharding the batch is the next step if a workload needs more.

## 2. The fix found by this benchmark

The first run gave **273k records/s** with one thread (1,687 ns per call, all of it spent waiting on a full queue).
A probe isolated the cause: on this HDD, `fsync` of a 16 MiB file took **264 ms**, while writing it took 7 ms; the
writer fsynced every rotated file (every 16 MiB) and, in `Balanced`, once a second, on its own thread. Without
rotation the same writer did 5.0 M records/s.

Rotated files and the periodic `Balanced` fsync now go to a separate durability thread (as in the .NET store since
2026-11-18). `flush(fsync)`, close and `Durable` still wait for it before they report `FsyncConfirmed`
(`tests/local.rs`: `contexts_are_defined_once_per_file_and_again_after_rotation`). Result: 273k → 4.6 M records/s
(16.9×) with one thread.

## 3. Not measured

- Linux, other disks, a machine without the known instability.
- Server mode throughput over a real network (shipping is tested for correctness: `tests/server.rs`,
  `SdkTests.Rust_sdk_spools_offline_and_ships_exactly_once_with_exact_types`).
- Response time under a fixed rate (coordinated omission), as `latency-v1.md` does for .NET.
- Memory use under sustained saturation (bounded by `queue_bytes` × 2 by design, not measured).
