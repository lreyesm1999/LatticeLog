# Latency at a fixed rate — v1 (P02-T08, P14-T03)

Date: 2026-11-18 · `dotnet run -c Release --project benchmarks/LatticeLog.Benchmarks -- latency` · Intel Core
i9-14900KF, Windows 11 10.0.26300, .NET 10.0.15, local SSD with the system antivirus active. **One machine**: the
numbers do not generalize.

## Method

- Producers (1 or 4 threads) emit the benchmark's 4-field typed event at a **fixed** total rate (10k, 100k or 500k
  events/s), with 1 s of warm-up and 5 s measured.
- Two times are recorded per call and all samples are sorted (exact percentiles, no approximate histogram):
  - **service**: how long the log call takes;
  - **response**: from the moment the call *should* have happened according to the schedule until it returns. It
    includes the accumulated delay when an earlier call blocked, so it does not hide stalls (coordinated omission).
- Sinks: a real store (`StoreWriter`, 64 MiB segments) in `Balanced` and `Durable`, and a null sink as a reference.
- Clock (`Stopwatch`) resolution on Windows: 100 ns; values of 100–300 ns are at the limit of measurement.
- Default saturation policy (`Block`, a 65,536-slot queue).

## 1. Results (full run, before the fix in §4)

Values in µs. Service = duration of the call; response = from the scheduled instant.
`research/results/latency-20261118-104821.csv`.

| sink | mode | prod. | events/s | service p50 | p99 | p99.9 | max | response p99 | p99.9 | max | GC 0/1/2 | drops |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|---:|
| store | Balanced | 1 | 10,000 | 0.1 | 1.1 | 2.5 | 70 | 2.5 | 36 | 163 | 0/0/0 | 0 |
| store | Balanced | 1 | 100,000 | 0.1 | 0.3 | 4.0 | 67 | 2.5 | 40 | 159 | 1/1/1 | 0 |
| store | Balanced | 1 | 500,000 | 0.1 | 0.2 | 2.1 | 138 | 2.9 | 56 | 266 | 1/1/1 | 0 |
| store | Balanced | 4 | 10,000 | 0.3 | 1.4 | 2.9 | 35 | 2.3 | 36 | 232 | 0/0/0 | 0 |
| store | Balanced | 4 | 100,000 | 0.2 | 0.8 | 4.1 | 107 | 2.0 | 44 | 397 | 0/0/0 | 0 |
| store | Balanced | 4 | 500,000 | 0.2 | 0.5 | 2.8 | 224 | 3.6 | 79 | 1,313 | 1/1/0 | 0 |
| store | Durable | 1 | 10,000 | 0.1 | 0.9 | 1.7 | 82 | 6.5 | 110 | 342 | 0/0/0 | 0 |
| store | Durable | 1 | 100,000 | 0.1 | 0.4 | 1.3 | 2,114 | 4.2 | 102 | 2,114 | 1/0/0 | 0 |
| store | Durable | 1 | 500,000 | 0.1 | 0.2 | 0.8 | 4,181 | 7.2 | 215 | 4,181 | 1/1/0 | 0 |
| store | Durable | 4 | 10,000 | 0.3 | 1.0 | 1.6 | 34 | 1.5 | 59 | 192 | 0/0/0 | 0 |
| store | Durable | 4 | 100,000 | 0.2 | 0.9 | 2.2 | 1,968 | 4.6 | 107 | 1,968 | 3/3/1 | 0 |
| store | Durable | 4 | 500,000 | 0.3 | 0.6 | 1.9 | 171 | 5.9 | 87 | 252 | 0/0/0 | 0 |
| null | Balanced | 1 | 10,000 | 0.1 | 1.3 | 2.8 | 34 | 4.9 | 85 | 338 | 0/0/0 | 0 |
| null | Balanced | 1 | 100,000 | 0.1 | 0.5 | 5.1 | 251 | 8.8 | 118 | 756 | 0/0/0 | 0 |
| null | Balanced | 1 | 500,000 | 0.1 | 0.3 | 2.4 | 230 | 8.3 | 121 | 303 | 0/0/0 | 0 |
| null | Balanced | 4 | 10,000 | 0.3 | 1.5 | 5.0 | 165 | 5.7 | 97 | 210 | 0/0/0 | 0 |
| null | Balanced | 4 | 100,000 | 0.3 | 0.9 | 6.4 | 74 | 5.8 | 88 | 248 | 0/0/0 | 0 |
| null | Balanced | 4 | 500,000 | 0.3 | 0.6 | 4.4 | 171 | 6.5 | 106 | 2,521 | 0/0/0 | 0 |

## 2. Reading

- In this run (machine idle) **there were no long stalls**: the worst service time was 4.2 ms (`Durable`, 1 producer,
  500k/s) and in `Balanced` the worst delay relative to the schedule was 1.3 ms. Earlier runs of the same configuration
  did have them (§3): the phenomenon is **intermittent**, and a single clean run does not rule it out.
- A typical call costs 0.1–0.3 µs (p50, at the limit of the clock resolution) and p99 ≤ 1.5 µs in every case, also with
  a real disk: the producer does no I/O.
- The **response** tail (p99.9 of 35–215 µs) is longer than the **service** tail: it reflects OS scheduler
  interruptions of the producer thread itself (they also show up with the null sink), not the logger.
- 0 drops in every configuration and very few GC collections (the benchmark's own sample arrays already add up to 40 MB
  per point).
- It is not a maximum-throughput measurement: the store writer sustains ~3M records/s on this machine (§3).

## 3. Diagnosing the writer-thread stalls

Repeated configuration: store, `Balanced`, 1 producer, 500k events/s, 5 s.

| variant | runs | runs with a stall > 10 ms | worst service max | worst delay vs schedule |
|---|---:|---:|---:|---:|
| fsync every 1 s **and** every 8 MiB, 64 MiB segments | 6 | 5 | 1.51 s | 6.2 s |
| fsync every 1 s (current default), 64 MiB segments | 4 | 1 | 252 ms | 252 ms |
| same, the full run from §1 | 1 | 0 | 0.14 ms | 0.27 ms |
| fsync every 8 MiB + 4 GiB segments (no rotation) | 4 | 3 | 2.08 s | 8.2 s |

(The first row was measured with the 8 MiB volume-based fsync, which was the provisional default during the
investigation; today it is off by default. The samples are small: 4–6 runs per variant.)

Direct measurement of the store writer without a logger (8 s at full speed, 3.2M records/s, fsync every 1 s): batches
of 1,024 records with p50 0.20 ms and p99 0.99 ms, but **18–137 ms batches when sealing and rotating** and up to
**330 ms on periodic fsyncs**.

Conclusions:
- The writer thread blocks on I/O (fsync, sealing and probably the write itself when the system throttles dirty pages).
  With the `Block` policy, a stall longer than `QueueCapacity / rate` (131 ms at 500k/s) propagates to the producers.
  That is the contract (bounded memory), but on this machine it happens often.
- **The cause is not identified**: there are stalls with and without rotation, and with and without volume-based fsync.
  Candidates: Windows cache manager write throttling, antivirus scans when segments are created or closed, SSD firmware.
  Nothing is ruled out without an ETW/WPR trace.
- Volume-based fsync (`BalancedFsyncBytes`) did **not** show a consistent improvement; it stays as an option that is
  off by default (it bounds the data at risk, not latency).
- `Durable` behaved better than `Balanced` at 500k/s with 4 producers (small, frequent fsyncs), although with 1 producer
  it also had a 9 ms peak.

Next steps at the time: an ETW/WPR trace of a run with a stall; the same measurement on Linux (CI) and with the
antivirus excluding the directory; moving fsync and sealing off the writer thread (data already written to a segment
does not depend on sealing to be readable), and evaluating whether `Block` should stay the default policy.

## 4. Cause found and fix (2026-11-18, second investigation)

**Instrumentation.** Every slow operation of the writer thread and the store (write, fsync, seal, segment creation,
manifest) is reported with its duration (`SlowWriterOperationThreshold`, `StoreWriterOptions.Diagnostics`). Every GC
pause is read from the runtime itself (`EventListener`, from SuspendEE to RestartEE). Everything is merged into a
timeline together with the producers' stalls (`latency --diag`).

**What ~60 instrumented runs showed** (store, 500k/s, 1 and 4 producers):

| observation | measured values |
|---|---|
| GC pauses | 2–6.6 ms in every run: **ruled out** as the cause |
| fsync of the active segment | usually 50–330 ms; peaks of **1.8 s** and **5.3 s** |
| segment seal (footer + fsync + close) | 171 ms, 462 ms, **3.8 s** |
| 32 KB write to the active segment | takes **exactly as long** as the in-flight fsync of the same file (e.g. 298.6 ms vs 299.9 ms; 1,834.6 ms vs 1,836.3 ms) |
| write with no fsync in flight | one case of 755 ms |

Conclusion: the cause is **latency spikes of the storage device**. On top of that, on Windows/NTFS a write to a file
waits while that same file is being flushed. The machine's SSD (NVMe, drive C:) was at **98 % capacity** (≈ 11 GB free
of 476 GB). That is the most likely explanation for multi-second latencies, because a nearly full SSD has much higher
write amplification, but it is **not verified**: there is no other SSD in the machine to compare with (D: is an HDD).

**Fix applied: background durability.** Sealing rotated segments and the periodic `Balanced` fsync now run on a
store-owned thread (`DurabilityWorker`, FIFO, merged fsync requests, sticky failure). Segments are written with
`RandomAccess` on an OS handle, so the fsync can run in parallel. Explicit `FlushAsync(FsyncConfirmed)` calls and
`Durable` mode still wait for the fsync, as their contract says. Tests: `BackgroundDurabilityTests` (simulated slow or
failing disk).

| configuration (Balanced, 500k/s) | before: runs with a stall > 10 ms | after |
|---|---:|---:|
| 1 producer | 4 of 12 (worst 123 ms) | **0 of 12** (worst 2.5 ms) |
| 4 producers | 2 of 6 | 1 of 6 in one batch; 6 of 8 in another, with disk flushes of up to 5.3 s |

An honest reading:
- Seals no longer block the writer. The periodic fsync does not either, **except** for the NTFS rule: while the active
  file is being flushed, writing to it waits. With a disk that takes seconds to flush, the writer still stops for that
  long and, with the queue full, so do the producers (up to `BlockTimeout`; in the official run below, 5 s and 4 records
  dropped and **counted**).
- **Tried and rejected**: background volume-based fsync (every 4 or 8 MiB). It makes things worse, because each flush
  costs 50–230 ms regardless of size and each one opens another stall window. A 4× larger queue (262,144 slots) gave no
  demonstrable improvement over 5–8 runs.
- Open:
  - repeat on another machine or a disk with free space, and on Linux, where the page cache does not block writes like
    this during an `fsync`;
  - consider splitting encoding and I/O, so the writer accumulates already-encoded batches (~32 B per record instead of
    ~300 B per object) while the disk is blocked;
  - document queue sizing relative to `BlockTimeout` and `DropNewest`.

**Full official run after the fix** (`research/results/latency-20261118-112854.csv`, µs):

| sink | mode | prod. | events/s | service p50 | p99 | p99.9 | max | response p99 | p99.9 | max | GC 0/1/2 | drops |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|---:|
| store | Balanced | 1 | 10,000 | 0.1 | 0.9 | 1.8 | 33 | 8.5 | 118 | 280 | 0/0/0 | 0 |
| store | Balanced | 1 | 100,000 | 0.1 | 0.3 | 3.2 | 52 | 3.5 | 44 | 217 | 1/1/1 | 0 |
| store | Balanced | 1 | 500,000 | 0.1 | 0.2 | 2.0 | 448 | 3.0 | 52 | 448 | 1/1/1 | 0 |
| store | Balanced | 4 | 10,000 | 0.3 | 0.9 | 1.6 | 25 | 1.5 | 27 | 169 | 0/0/0 | 0 |
| store | Balanced | 4 | 100,000 | 0.2 | 0.5 | 3.0 | 106 | 1.0 | 30 | 509 | 0/0/0 | 0 |
| store | Balanced | 4 | 500,000 | 0.3 | 0.9 | 161 | 5,000,003 | 5,392,415 | 5,431,773 | 5,435,489 | 4/3/1 | 4 |
| store | Durable | 1 | 10,000 | 0.1 | 0.9 | 1.4 | 40 | 3.8 | 92 | 395 | 0/0/0 | 0 |
| store | Durable | 1 | 100,000 | 0.1 | 0.6 | 1.3 | 1,513 | 7.5 | 136 | 1,513 | 4/4/0 | 0 |
| store | Durable | 1 | 500,000 | 0.1 | 0.1 | 16 | 120,022 | 2,616,377 | 2,624,910 | 2,627,545 | 1/1/0 | 0 |
| store | Durable | 4 | 10,000 | 0.3 | 1.4 | 5.1 | 20 | 5.1 | 68 | 1,726 | 1/1/0 | 0 |
| store | Durable | 4 | 100,000 | 0.2 | 0.5 | 1.1 | 75 | 1.3 | 28 | 148 | 0/0/0 | 0 |
| store | Durable | 4 | 500,000 | 0.2 | 0.4 | 1.0 | 1,342 | 2.3 | 57 | 1,342 | 1/0/0 | 0 |
| null | Balanced | 1 | 10,000 | 0.1 | 1.1 | 2.5 | 64 | 2.4 | 52 | 1,116 | 0/0/0 | 0 |
| null | Balanced | 1 | 100,000 | 0.1 | 0.3 | 3.2 | 85 | 2.3 | 39 | 212 | 0/0/0 | 0 |
| null | Balanced | 1 | 500,000 | 0.1 | 0.2 | 1.9 | 94 | 3.3 | 36 | 169 | 0/0/0 | 0 |
| null | Balanced | 4 | 10,000 | 0.3 | 0.9 | 1.9 | 52 | 1.8 | 39 | 138 | 0/0/0 | 0 |
| null | Balanced | 4 | 100,000 | 0.2 | 0.6 | 3.3 | 146 | 1.5 | 38 | 218 | 0/0/0 | 0 |
| null | Balanced | 4 | 500,000 | 0.2 | 0.4 | 2.6 | 417 | 2.8 | 84 | 2,164 | 0/0/0 | 0 |

With the null sink, with no disk, there are no stalls in any configuration: the logger itself does not cause them.
