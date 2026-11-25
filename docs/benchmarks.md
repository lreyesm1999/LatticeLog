# Benchmarks: what was measured, how, and what it does not show

This page summarizes the measurement reports in [`reports/`](../reports). The reports are the source of truth: each
one has its raw data (CSV in [`research/results/`](../research/results)), its method and an "honest reading" section
that lists where LatticeLog loses. Nothing here was re-measured for this page.

## Hardware

| | |
|---|---|
| CPU | Intel Core i9-14900KF (32 logical threads) |
| Memory | 64 GiB |
| Disk | Local NVMe SSD (drive C:), **98 % full** during the latency runs, system antivirus active |
| OS / runtime | Windows 11 (10.0.26300), .NET 10.0.15 x64, Release builds, workstation GC |
| Date | 2026-11-18 |

**Known problem with this machine:** under all-core load it shows random crashes that a control program with no
LatticeLog code also reproduces at 16 threads (the known Raptor Lake instability with pre-fix microcode is the likely
cause; not proven). Concurrency runs were therefore pinned to 4 CPUs. **All results are from this one machine and do
not generalize.** Repeating them on stable hardware and on Linux is an open roadmap item.

## Results

| Report | Question | Headline | Where LatticeLog loses or the result is limited |
|---|---|---|---|
| [Hot path](../reports/benchmarks-hotpath.md) | Cost of a log call on the producer thread | Enabled typed event **55.6 ns / 5 B**; disabled **1.12 ns / 0 B**; free text + 2 attributes 63.1 ns / 105 B | Null sink (no I/O), one producer thread; a disabled MEL `LoggerMessage` is still faster (0.12 ns) |
| [Latency at a fixed rate](../reports/latency-v1.md) | Latency of the log call under a sustained rate, with a real store | p50 0.1–0.3 µs, p99 ≤ 1.5 µs at 10k–500k events/s, 0 drops in the full run | **Intermittent stalls of up to seconds**, traced to storage-device latency on a nearly full SSD; mitigated (background durability), not eliminated |
| [Baseline v1](../reports/baseline-v1.md) | Size and selective queries on 8 synthetic workloads (100k records each) | `.llog` (Lattice-S + DEFLATE) smaller than JSONL + zstd-3 on **7 of 8** workloads (−1.4 % to −23.4 %); time/severity index pruning **20–40×** faster than a full scan; a full scan 4–9× faster than JSONL + zstd decode + parse + filter | JSONL + zstd-19 is smaller on **every** workload (but takes 9–16 s per 100k records); the −30 % target was **not** met; `adversarial-random` is +9.2 % |
| [Loghub-2.0](../reports/loghub2-v1.md) | Size on 14 public datasets | Typed events **−25 %** vs the same content as CSV + zstd-3 (−5 % to −58 %) | **+17 %** vs CSV + zstd-19; byte-exact raw text is **+83 %** vs zstd-3; templates come from Loghub's ground truth, so real-world results will be worse |
| [Ingest v1](../reports/ingest-v1.md) | Server ingestion throughput | ~**1.15 M records/s** with 4 producers and the binary body (2.0–3.4× the JSONL body) | Loopback on one machine; a real network between machines is untested |
| [MCP evidence V2](../reports/mcp-optimization-v2.md) | MCP latency, transmitted bytes, estimated tokens and known-evidence recovery across scenarios A–G | Repetitive-error transport **−39 %** and a 2 KB bounded pack **−52 %**, with all evidence checks passing | 10k/100k/1M scale checks expose poor in-segment correlation pruning at 1M; repetitive-error p95 **+6.3 %** at 100k; token counts use UTF-8 bytes/4, not a model tokenizer |

## Versions and datasets

- **Benchmark harness:** BenchmarkDotNet 0.15.8 (in-process toolchain) for the hot path; a custom fixed-rate harness
  (`benchmarks/LatticeLog.Benchmarks -- latency`) that records every sample and measures response time from the
  scheduled instant, so stalls are not hidden (coordinated omission).
- **Synthetic workloads:** 8 deterministic generators with seed 1 (`research/LatticeLog.Lab/Workloads.cs`). Every
  lossless variant is verified by the SHA-256 of the rebuilt canonical JSONL.
- **Loghub-2.0:** Zenodo record 8275861 (CC-BY-4.0; Jiang et al., ISSTA 2024), MD5-verified, first 200,000 lines of each
  dataset, read from the zips. Manifest: [`research/dataset-manifest.yaml`](../research/dataset-manifest.yaml).
- **Format version:** sizes in baseline §6 and Loghub are format 0.2; format 0.3 (block summaries) was added later and
  has not been re-measured for size.

## Reproduce

```bash
scripts/reproduce.sh            # tests, baseline, latency benchmark and, if the dataset is present, Loghub-2.0
pwsh scripts/reproduce.ps1      # same on Windows

dotnet run -c Release --project research/LatticeLog.Lab -- baseline --events 100000 --reps 3 --out research/results
dotnet run -c Release --project research/LatticeLog.Lab -- loghub --lines 200000
dotnet run -c Release --project benchmarks/LatticeLog.Benchmarks -- --filter '*Enabled*'
dotnet run -c Release --project benchmarks/LatticeLog.Benchmarks -- latency
dotnet run -c Release --project benchmarks/LatticeLog.Benchmarks -- mcp --events 100000 --iterations 15
```

Synthetic sizes should match byte for byte on another machine with the same runtime (DEFLATE and zstd may vary between
library versions). Timings will not.

## Known limitations of these measurements

- One machine, one OS, and that machine is unstable under load.
- Most timing tables are single runs or 3 repetitions; `.llog` encode/decode times in baseline §2 are flagged as
  unreliable by the report itself.
- The hot-path numbers exclude I/O by design; the latency report covers I/O.
- No comparison with other logging libraries' hot paths (Serilog, NLog) has been run. Claims of the form "faster than
  X" are therefore not made anywhere in this repository.

Contributions of runs on other hardware are welcome: open an issue with the report's command, your hardware and the
output.
