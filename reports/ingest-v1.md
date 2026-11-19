# Remote ingestion v1 — functional evidence and end-to-end measurement (P16-T13, P16-T14)

Date: 2026-11-18 · The same machine as the other reports (i9-14900KF, Windows 11, .NET 10.0.15, SSD at 98 %
capacity). Collector and client on **loopback**: there is no real network in between.

## 1. Functional tests (`tests/LatticeLog.Tests/IngestTests.cs`, all green 3 times in a row)

| test | what is injected | what is checked |
|---|---|---|
| lossy network | 20 % of requests lost, 20 % of responses lost **after** the server stored the batch, 10 % delayed; 12,000 records in batches of 700 | the server holds every record **exactly once**, in local order, with its original context under the `ingest` one |
| collector crash | it stops halfway without a checkpoint; the client also loses its cursor and resends everything from 1 | deduplication is rebuilt from the data and the final result has 6,000 records, none twice |
| deduplication rules | repeated batch, overlapping batch, gap, declared gap | `duplicate` / `partial_duplicate` / 409 `sequence_gap` / `gap_before` |
| security and limits | no token, wrong token, disallowed producer, altered CRC, unknown encoding, gzip bomb (4 MB of spaces), oversized request | 401 / 401 / 403 / 400 / 415 / 413 / 413, and the server stays healthy afterwards |
| rotation and quotas | the tokens file is changed while running; a token limited to 100 records/s | the old token gets 401 and the new one 200, with no restart; 429 with `Retry-After` |
| HTTP on a public address | `http://0.0.0.0` without `AllowInsecureHttp` | refuses to start |

## 2. End-to-end performance (`benchmarks … -- ingest`)

300,000 typed events (4 fields), batches of 5,000, **one batch in flight** (like the forwarder), warm-up discarded. Two
runs:

| body | ACK | records/s | MB/s on the wire | bytes/record | ACK p50 ms | ACK p99 ms |
|---|---|---:|---:|---:|---:|---:|
| jsonl+gzip | FsyncConfirmed | 195,277 / 191,984 | 5.7 | 29.2 | 13.9 / 14.8 | 48.9 / 43.7 |
| jsonl | FsyncConfirmed | 179,028 / 181,828 | 42 | 233.4 | 14.9 / 14.0 | 66.7 / 45.3 |
| jsonl+gzip | WrittenToOs | 216,728 / 215,191 | 6.3 | 29.2 | 12.3 / 12.6 | 42.8 / 39.8 |
| jsonl | WrittenToOs | 208,701 / 216,242 | 49 | 233.4 | 13.1 / 12.2 | 49.1 / 40.8 |

Reading:
- **gzip pays off**: traffic drops 8× (29 vs 233 bytes per record) with no measurable throughput cost.
- Asking the server for fsync costs ~10 % of throughput with batches of 5,000. *Group commit* amortizes it when there
  are several producers (measured in §3).
- The ~200k records/s ceiling with one batch in flight is set by JSONL encoding and decoding plus the round trip, not by
  the disk. The improvements were a binary `.llog` body (done: §5) and several batches in flight per producer (it would
  require reordering on the server; not done).
- **Not measured**: a real network with latency and TCP-level loss, TLS, and isolated server memory under load
  (concurrent producers are in §3). The forwarder's incremental read of the active segment has since been implemented
  (`StoreTail`).

## 3. Many producers at once (2026-11-18, second measurement)

`benchmarks … -- ingest --affinity FF --records 320000 --producers 1,4,16,64 --batch 1000`: P producers with distinct ids
and one batch in flight each, the same total volume, gzip and fsync ACK. Envelopes are encoded before measuring, so
this measures the collector (transport, decoding, deduplication and group commit), not the clients. Each run checks
that the server stored exactly every record. The process is pinned to **8 logical CPUs** (see the note about the
machine). Two runs:

| producers | records/s | ACK p50 ms | ACK p99 ms | ACK max ms | batches per fsync | process peak memory MiB |
|---:|---:|---:|---:|---:|---:|---:|
| 1 | 215,227 / 233,859 | 4.0 / 3.8 | 30.4 / 16.2 | 46 / 22 | 1.0 | 359 / 333 |
| 4 | **575,861 / 636,266** | 6.1 / 5.7 | 28.9 / 24.6 | 47 / 37 | 1.3 / 1.2 | 360 / 336 |
| 16 | 443,889 / 437,860 | 33.4 / 34.0 | 75.5 / 77.6 | 96 / 94 | 5.8 / 5.6 | 403 / 387 |
| 64 | 347,390 / 373,267 | 165.6 / 174.3 | 287.5 / 284.1 | 288 / 284 | 22.9 / 26.7 | 507 / 482 |

Reading:
- **Group commit works**: with 64 producers, each fsync confirms 23–27 batches.
- **Peak with 4 producers** (~600k records/s). With more, the total drops and ACK latency grows almost proportionally
  (queueing on the single writer thread). Clients and server share the 8 CPUs, so the split between decoding CPU and
  the writer was not separated. Next step at the time: profile the collector alone and move decoding and the provenance
  wrapper off the writer thread (done: §5).
- Peak memory includes the benchmark's own pre-encoded envelopes and source records, not only the server.

### Note about the reference machine (affects every measurement with many threads)

With 16 or more busy threads, the benchmark process crashed at random: segmentation faults, a *stack overflow* inside
`DateTimeOffset.TryParse` and impossible exceptions inside the .NET JSON parser. A **control program with no LatticeLog
code** (gzip + JSON + dates, `tools/platform-stress`: `dotnet run -c Release --project tools/platform-stress -- 16 30`)
reproduces the same with 16 threads, and runs without errors with 1, 4 and 8 threads (tens of millions of lines
checked).

The machine has an Intel Core i9-14900KF with **microcode 0x11D** and BIOS F4 from 2023-09-11 (Gigabyte Z790 AORUS
ELITE X WIFI7). That predates the updates with which Intel fixed the instability of 13th/14th-generation Core CPUs
(0x125 onwards). It is the most likely explanation, **but it is not proven**.

Recommendation to the owner:
1. Update the BIOS to the manufacturer's latest version, which ships the fixed microcode.
2. Repeat the control.
3. If it still fails, consider Intel's extended warranty for this issue.

Until then, many-thread measurements on this machine are taken with a pinned CPU set, and results with more
concurrency have to be repeated on another machine or in CI.

## 4. Producers and readers at once (P17-T16)

`benchmarks … -- ingest --records 320000 --producers 4,16 --batch 1000 --readers 0|4`, with the process pinned to **4
logical CPUs** (with 8, the instability from §3 still showed up: an `AccessViolationException` and a
`NullReferenceException` inside .NET loops; with 4 CPUs, 0 failures across all runs). Each reader alternates an
attribute search that no index can prune and an aggregation by event over the namespace being written.
*Erratum (2026-11-18)*: the filter was written wrongly (`attributes.quantity` instead of `attr.quantity`) and returned
no results. The measured cost is still that of walking the whole namespace, which is what was meant to be measured, but
those searches returned no events. `attributes.` is now accepted as an alias. Two runs per configuration:

| producers | readers | ingested records/s | ACK p50 ms | ACK p99 ms | queries | query p50 ms | query p99 ms |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 4 | 0 | 290,895 / 287,102 | 12.1 / 12.7 | 43.8 / 38.0 | — | — | — |
| 16 | 0 | 383,780 / 372,944 | 41.2 / 37.9 | 78.4 / 102.5 | — | — | — |
| 4 | 4 | 135,393 / 127,753 | 23.4 / 25.0 | 78.8 / 95.0 | 42 / 44 | 174 / 156 | 865 / 1,066 |
| 16 | 4 | 168,041 / 202,484 | 71.7 / 71.3 | 278.6 / 167.4 | 23 / 29 | 270 / 167 | 845 / 741 |

Reading:
- Reads do not block ingestion (every query and every ACK completed), but they **compete for CPU**: with 4 CPUs and 2
  concurrent queries (the default, half the CPUs), ingestion throughput halves. If ingestion takes priority, set
  `QueryLimits.MaxConcurrentQueries = 1`. There are no CPU priorities between the two paths yet.
- The queries used walk the whole namespace (no index pruning) while it grows to 320,000 records; time or severity
  queries are pruned by index and are much cheaper (`reports/baseline-v1.md` §3).
- This test found a real bug: unexpected read errors came out as 500s with no trace. They are now reported through
  `ServerOptions.Diagnostics` (type, message and stack trace on the server; only type and message to the client).

## 5. Binary body vs JSONL (2026-11-18)

The same benchmark as §3 (pre-encoded envelopes, gzip, fsync ACK, batches of 1,000), process pinned to **4 CPUs**,
`--encoding 1` (JSONL) vs `--encoding 2` (binary mini segment: catalog + Lattice-S). Two runs:

| producers | JSONL records/s | binary records/s | binary ACK p50 ms | binary ACK p99 ms | batches per fsync (binary) |
|---:|---:|---:|---:|---:|---:|
| 1 | 234,615 / 232,327 | **561,249 / 470,147** | 1.6 / 2.1 | 2.9 / 3.4 | 1.0 |
| 4 | 512,920 / 517,415 | **1,155,975 / 1,153,816** | 3.0 / 3.1 | 14.0 / 13.2 | 2.0 |
| 16 | 422,723 / 432,953 | **1,450,214 / 1,454,228** | 10.4 / 10.5 | 19.0 / 28.2 | 7.8 |

Reading:
- **2.0–3.4× more throughput** and less memory (≈240 MiB vs a 345–400 MiB process peak). JSONL decoding dominated the
  server's cost. With the binary body, the 16-producer ceiling no longer drops relative to 4.
- Pre-encoding hides the client's cost. Encoding to binary is also cheaper than to JSONL, but that was not measured
  separately.
- It is still a 4-CPU run on a machine with stability problems (§3): to be repeated on stable hardware.
