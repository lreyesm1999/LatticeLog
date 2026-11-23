# TypeScript SDK: producer cost and throughput, v1 (P21)

Date: 2026-11-22. Command: `node bench/bench.mjs D:/latticelog-bench/ts 500000` (`sdks/typescript/bench/bench.mjs`).
Each scenario runs in its own child process.

Conditions:

- Intel Core i9-14900KF, Windows 11 10.0.26300, Node.js 26.13.1, pino 9.5.0.
- Process affinity 0xFF (8 logical CPUs), because the machine is unstable under all-core load (as in
  [`rust-sdk-v1.md`](rust-sdk-v1.md)).
- Files on a **7,200 rpm SATA HDD** (Seagate ST1000DM010, drive D:). A control run used the NVMe SSD (ADATA LEGEND
  700, drive C:, 98 % full).
- System antivirus active.
- **One machine. The full set ran twice; the SSD control ran twice per row.** The numbers do not generalize.

## Method

- **Record.** Every scenario writes the same logical record: the message `order placed` and 3 attributes (int, float,
  string).
  - SDK: `log.info("order placed", { order_id: i, total: 19.99, sku: "A-1" })`.
  - pino: `logger.info({ order_id: i, total: 19.99, sku: "A-1" }, "order placed")`.
- **Event loop.** The producer yields to the event loop (`await setImmediate`) every 1,000 records, as a server does
  between requests. Asynchronous writers (the SDK's, and pino's sonic-boom in async mode) can only write while the loop
  turns. The *burst* rows never yield.
- **call**: time spent inside the logging calls, per record (`process.hrtime.bigint()` around blocks of 1,000).
- **end to end**: records written ÷ time from the first call until everything is handed to the OS. For the SDK that
  is `await log.flush()`; for pino, `dest.flush(cb)` or the transport's `close`. No fsync is waited for, except in
  Durable mode.
- **paced**: the producer follows a schedule of 100,000 records/s (batches of 1,000, timers).
- **latency**: 200,000 single calls, each timed individually. That includes ~30–60 ns of timer overhead, and the clock
  resolution is ~100 ns.
- **disabled**: `debug` calls with an `info` minimum, 10 M calls.
- **Baselines.**
  - `pino.destination({ sync: false, minLength: 4096 })` (sonic-boom, async).
  - `pino.destination({ sync: true })`.
  - `pino.transport({ target: "pino/file" })`, a worker thread.
  - pino → the SDK's `latticelog/pino` destination.
- **Durability differs.** pino never fsyncs. The SDK's Balanced mode fsyncs once a second, and Fast mode only at
  flush, rotation and close. Fast is the like-for-like row.

## 1. Results on the HDD (run 1 / run 2)

| scenario | call ns/record | end to end records/s | written of 500,000 (dropped) | peak queue (records) | bytes/record | max RSS MiB |
|---|---:|---:|---:|---:|---:|---:|
| SDK, Balanced | 350 / 345 | 964,934 / 757,939 | 465,593 (34,407) / 465,593 (34,407) | 114,912 / 114,912 | 192 | 340 / 352 |
| SDK, Fast | 341 / 388 | 1,165,020 / 891,822 | 500,000 (0) / 387,212 (112,788) | 85,700 / 114,912 | 192 | 242 / 268 |
| SDK, Durable (50,000 records) | 588 / 603 | 148,133 / 146,075 | all (0) | 40,000 / 39,000 | 190 | 152 / 151 |
| SDK, Balanced, paced 100k/s | 396 / 373 | 97,709 / 98,089 | all (0) | 17,000 / 27,000 | 192 | 150 / 159 |
| pino async, paced 100k/s | 1,395 / 1,361 | 92,031 / 96,554 | all | — | 140 | 140 / 139 |
| SDK, burst (no yield), `drop` | 409 / 430 | — | 115,673 (384,327) / same | — | 190 | 252 / 253 |
| SDK, burst (no yield), `block` | 953 / 949 | 856,061 / 888,302 | all (0) | — | 192 | 400 / 399 |
| pino async | 1,251 / 1,216 | 305,258 / 290,553 | all | — | 140 | 260 / 250 |
| pino sync | 2,332 / 2,319 | 411,253 / 415,315 | all | — | 140 | 100 / 100 |
| pino async, burst | 1,249 / 1,246 | 245,096 / 339,761 | all | — | 139 | 253 / 278 |
| pino transport (`pino/file`, worker) | 418 / 424 | 541,331 / 562,965 | all | — | 140 | 215 / 239 |
| pino → `latticelog/pino` | 1,707 / 1,691 | 409,737 / 424,966 | all (0) | — | 235 | 146 / 165 |

- **Single-call latency** (SDK / pino async): p50 300 / 900 ns, p99 1,000–1,200 / 3,000–3,100 ns, p99.9
  5.3–6.1 / 10.2–11.6 µs. The maximum was 0.2–0.3 ms for both (event-loop turns and GC).
- **Disabled level**: 1.68 ns per call (pino 2.71–2.77 ns). The attribute object literal is still evaluated:
  JavaScript evaluates arguments before the call, unlike the Rust macros.
- **Final flush after the last call**: pino async took 0.8–1.4 s to write its in-memory backlog in the flood rows
  (`final_flush_ms`). The SDK took 13–192 ms.
- **Bytes per record**: the SDK writes 192 bytes per record against pino's 140. The SDK adds the type, the sequence,
  the context reference and a 9-digit time. pino writes `pid` and `hostname` on every line, which the SDK keeps once
  in the resource context.

## 2. Results on the SSD (control, two runs)

| scenario | call ns/record | end to end records/s | dropped | peak queue |
|---|---:|---:|---:|---:|
| SDK, Balanced | 318 / 320 | 1,610,916 / 1,599,099 | 0 / 0 | 5,000 / 5,000 |
| SDK, Fast | 329 / 319 | 1,577,803 / 1,608,872 | 0 / 0 | 5,000 / 5,000 |
| pino async | 1,221 / 1,232 | 534,305 / 521,293 | — | — |

## 3. What the numbers say

- **The logging call costs 3.5× less than pino's** with the async destination (318–430 ns against 1,216–1,395 ns),
  and about the same as pino with a worker-thread transport (418–424 ns). In the transport's case, the encoding
  happens in pino's main-thread serializer plus the worker.
- **The flood rows are an overload test on the HDD.** The producers offer roughly 2.5 M records/s, about 480 MB/s of
  lines. On the SSD the writer keeps up with this load: the queue peaked at 5,000 records and nothing was dropped. On
  the HDD the writer stalls, and the queue fills.
  - The SDK bounds its memory (16 Mi characters, about 115,000 of these records). It drops the newest records, counted
    in `droppedSaturation`, or blocks the call with `saturation: "block"`.
  - pino keeps an unbounded backlog in memory and writes it after the producer stops.
  - Losing nothing is what `"block"` is for: 950 ns per call, still faster than pino's async call.
- **Why the writer stalls on the HDD.** Two causes were seen; the second was not isolated.
  - *Same-file fsync (measured).* On this NTFS volume an append waits while the same file is being fsynced. Writing
    1 MiB blocks while a 16 MiB `fdatasync` of the same file runs: 284 ms and 370 ms in two probes. Appends to *another*
    file were not delayed (max 0.7 ms). This is the Balanced mode's once-a-second fsync. The .NET store documents the
    same effect (`specs/durability.md`).
  - *Unexplained stall.* Fast mode, which has no periodic fsync, also stalled in run 2 and dropped 22.6 %. In run 1 it
    dropped nothing. The likely cause is Windows throttling writers while dirty cached data outruns the HDD; this was
    not verified.
- **At a steady 100,000 records/s** (19 MB/s) nothing was dropped. The queue peaked at 17,000–27,000 records, a
  170–270 ms backlog, during stalls.
- **Durable**: ~147k records/s with an fsync per batch on the HDD.

## 4. Fixes found by this benchmark

The first full run dropped 7–20 % of the records, although the writer's own code took about 1 % of the CPU profile's
samples. A trace of the queue over
time showed that the writer stalled at every 16 MiB rotation. Three causes were found and fixed:

1. **Segment sizing.** Chunks were sized from an estimate of the batch that ran out after a rotation. The writer then
   fell back to 1 KiB writes, about 5 records per thread-pool round trip. Chunks are now sized from the exact
   remainder of the batch.
2. **Synchronous open at rotation.** Opening the next file with `openSync` froze the event loop, and so the producers,
   for about 320 ms once. That happened while the previous file was being fsynced. The next file is now opened on the
   thread pool; synchronous opening remains only for the exit hook and `saturation: "block"`.
3. **Parallel fsyncs of rotated files.** On the HDD, each 16 MiB fsync takes about 250 ms, and four of them filled
   libuv's four threads, so the current file's writes waited. Rotated files are now fsynced one at a time, like the
   Rust SDK's durability thread. The periodic Balanced fsync is skipped while one runs.

## 5. Not measured

- Linux and macOS, other disks, and a machine without the known instability.
- Server mode throughput over a real network. Shipping is tested for correctness: `test/server.test.mjs`, and
  `SdkTests.TypeScript_sdk_spools_offline_and_ships_exactly_once_with_exact_types` against the real server.
- Response time under a fixed rate with coordinated omission accounted for, as `latency-v1.md` does for .NET.
- Node.js 20 and 22 performance. The CI runs the tests on Node.js 20 and 24, but the numbers are from Node.js 26 only.
- CPU profiles of the writer under load: only one profile was taken, the one that found the rotation stall.
