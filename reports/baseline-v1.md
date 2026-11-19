# Baseline v1 — storage, codecs, selective queries and P09 pre-study

> **Update 2026-11-18**: sections 1–5 measure format 0.1. Section 6 repeats the measurement with format 0.2 (catalog
> copy in the footer + compressed catalogs).

Date: 2026-11-18 · Generated with `research/LatticeLog.Lab` (`baseline --events 100000 --reps 3`) · Raw data:
`research/results/baseline-20261118-072257.{csv,md}` and `queries-20261118-072257.csv`.

**Environment**: Intel Core i9-14900KF (32 logical threads), 64 GiB RAM, Windows 11 (10.0.26300), .NET 10.0.15,
Release, workstation GC, local SSD, warm page cache, one thread. **One machine and one run**: the results do not
generalize (the plan's master rule).

**Data**: 8 deterministic synthetic workloads (seed 1), 100,000 records each. No public or real dataset here (Loghub-2.0
is measured separately in `reports/loghub2-v1.md`; see `research/dataset-manifest.yaml`).

**Comparison contract**: every *lossless* format was verified by the SHA-256 of the rebuilt canonical JSONL
(`roundtrip ok` on 100 % of rows). `.llog` sizes are the whole file: header, catalogs, footer index (bloom included) and
trailer. `text-lines` is only a scale reference (it loses information).

## 1. Total stored size (bytes, lower is better)

| workload | JSONL | JSONL+zstd-3 | JSONL+zstd-19 | JSONL+gzip | llog-raw | llog-S | llog-S+deflate | llog-C+deflate | S+deflate vs zstd-3 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| http-api | 36.8 M | 5.68 M | **4.27 M** | 5.50 M | 16.4 M | 5.92 M | 4.69 M | 4.70 M | **−17 %** |
| inventory | 37.4 M | 5.00 M | **3.91 M** | 4.96 M | 13.5 M | 7.57 M | 5.70 M | 5.60 M | **+14 % (worse)** |
| batch | 25.7 M | 2.01 M | **1.19 M** | 2.03 M | 10.8 M | 5.40 M | 1.79 M | 1.79 M | −11 % |
| iot | 20.9 M | 0.97 M | **0.64 M** | 1.47 M | 8.69 M | 3.28 M | 0.92 M | 0.91 M | −6 % |
| queue | 27.5 M | 2.71 M | **1.90 M** | 2.64 M | 10.2 M | 4.54 M | 2.07 M | 2.01 M | −23 % (C: −26 %) |
| microservices | 33.5 M | 3.87 M | **2.87 M** | 3.87 M | 12.1 M | 6.56 M | 3.50 M | 3.49 M | −10 % |
| high-cardinality | 36.2 M | 11.27 M | **10.25 M** | 11.48 M | 16.1 M | 11.72 M | 11.12 M | 11.11 M | −1 % |
| adversarial-random | 39.7 M | 21.68 M | **21.12 M** | 21.80 M | 23.55 M | 23.67 M | 23.67 M | 23.87 M | **+9 % (worse)** |

An honest reading:
- **Lattice-S+deflate is smaller than JSONL+zstd-3 on 6 of 8 workloads (−1 % to −23 %)** and worse on 2 (`inventory`
  +14 %: random per-record UUIDs that zstd models better as text; `adversarial` +9 %: random bytes).
- **JSONL+zstd-19 is the smallest on every workload**, but takes 9–16 s per 100k records (vs ~0.1 s for llog): it is
  an offline compression bound, not a live ingestion format (P02-T13).
- The aspirational target of **≥30 % fewer bytes than the best applicable baseline is NOT met** (best case −26 % vs
  zstd-3; vs zstd-19 llog is always larger). With its indexes included, the format also allows selective reads, which
  JSONL+zstd does not have (§3); both facts are published separately, as §6.1 of the plan requires.
- **Lattice-S vs plain binary (llog-raw)**: −40 % to −64 % without general-purpose compression on repetitive workloads;
  ≈0 on `high-cardinality` and +0.5 % on `adversarial` (the dictionary does not help). Gate G7: **met** for repetitive
  workloads, with a correct fallback (DEFLATE is skipped automatically when it does not shrink: `adversarial` raw =
  raw+deflate).
- **Lattice-C vs Lattice-S**: without general-purpose compression it wins −3 % to −21 % (`queue`, `inventory`,
  `microservices`), but after DEFLATE the advantage drops to 0–3.5 %. Gate G8: **positive but small net savings**; kept
  as a planner option, not the default.

## 2. Encoding/decoding times

`.llog` times include writing and re-reading a temporary file and showed **high variance** (e.g.
`high-cardinality/llog-structural+deflate` min 153 ms, median 645 ms, max 714 ms; outliers probably from I/O or
antivirus). With 3 repetitions they are **not considered reliable**; they will be repeated on Linux without antivirus
and with more repetitions. The decoding column is also **not comparable**: for `.llog` it includes materializing objects
and re-serializing to JSONL (≈150–300 ms / 100k), while for JSONL+zstd it only measures decompression without parsing.
The fair read comparison is the one in §3 (decode + filter).

## 3. Selective queries (ms, median of 3; "segments" = read/total)

| workload | query | hits | llog indexed | segments | llog full scan | JSONL+zstd decode+parse+filter |
|---|---|---:|---:|---:|---:|---:|
| http-api | trace_id | 2 | 24.3 | 1/3 | 51.1 | 265 |
| http-api | level ≥ ERROR | 985 | 47.4* | 3/3 | 36.2 | 252 |
| http-api | time 1 % | 1,006 | 1.5 | 1/3 | 36.4 | 255 |
| inventory | trace_id | 5 | 25.5 | 1/3 | 66.2 | 294 |
| inventory | time 1 % | 994 | 1.9 | 1/3 | 56.9 | 370 |
| microservices | trace_id | 3 | 26.9 | 1/2 | 35.9 | 220 |
| high-cardinality | trace_id | 1 | 17.3 | 1/5 | 60.5 | 238 |
| high-cardinality | time 1 % | 988 | 1.9 | 1/5 | 70.1 | 224 |
| batch/iot/queue | level ≥ ERROR (0 hits) | 0 | ≈0.0 | 0/1 | 19–23 | 180–192 |

Every query returned the same results with all three methods (`consistent`). Findings:
- A full `.llog` scan is **4–9× faster** than decompressing and parsing JSONL+zstd with the same filter.
- Time and severity indexes give **20–40×** over the full scan when they prune; the `trace_id` bloom filter prunes
  segments (1/3, 1/5) but the block is still decoded (no per-block trace index).
- *Regression found*: with segment-level pruning only (`errors`), random block reads were slower than sequential ones
  (47 vs 36 ms). **Fixed** after the measurement: if no block can be pruned, the segment is read sequentially (change in
  `QueryEngine.Blocks`; the measurement has not been repeated yet).

## 4. Lattice-P pre-study (gate G9)

Adaptive code lengths (they learn the model online and include the cost of learning it): order-0 vs order-1 (the
previous event as context) for the event identity, and Δt vs ΔΔt (µs) for timestamps.

| workload | event bits/rec o0 | o1 | Δt bits/rec | ΔΔt bits/rec | optimistic savings | % of S+deflate |
|---|---:|---:|---:|---:|---:|---:|
| http-api | 0.081 | 0.081 | 11.47 | 12.69 | 0 B | 0.00 % |
| inventory | 0.178 | 0.178 | 12.18 | 13.71 | 3 B | 0.00 % |
| batch | 0.069 | 0.035 | 7.65 | 8.44 | 417 B | 0.02 % |
| iot | 0.001 | 0.001 | 0.001 | 0.001 | 0 B | 0.00 % |
| queue | 1.187 | 0.187 | 6.37 | 11.23 | 12,495 B | 0.60 % |
| microservices | 0.001 | 0.001 | 12.23 | 13.52 | 0 B | 0.00 % |
| high-cardinality / adversarial | — | — | — | — | ≤ 4 B | 0.00 % |

**G9 decision: do not build Lattice-P** (a valid negative result according to the plan). The optimistic bound does not
exceed 0.6 % of the S+deflate size on any workload; the dictionary + DEFLATE already capture transition modelling.
Limitation: residuals of numeric values (P09-T06/T07, e.g. temperatures in `iot`) and real data were not evaluated;
reopen if real datasets show long, deterministic sequences.

## 5. Reproduction

```
dotnet run -c Release --project research/LatticeLog.Lab -- baseline --events 100000 --reps 3 --out research/results
```
or `scripts/reproduce.ps1` / `scripts/reproduce.sh`. The workloads are deterministic; sizes should match byte for byte
on another machine with the same runtime (DEFLATE/zstd may vary between library versions).

## 6. Format 0.2 (P04-R1) — full repetition

Same machine, workloads and parameters (`research/results/baseline-20261118-080231.{csv,md}`, `queries-…080231.csv`).
Every row `roundtrip ok`, every query consistent.

| workload | S+deflate 0.1 | S+deflate 0.2 | change | C+deflate 0.2 | JSONL+zstd-3 | S+deflate 0.2 vs zstd-3 |
|---|---:|---:|---:|---:|---:|---:|
| http-api | 4,694,813 | 4,694,966 | +0.00 % | 4,696,575 | 5,682,667 | **−17.4 %** |
| inventory | 5,704,198 | 3,972,773 | **−30.4 %** | 3,870,048 | 4,997,270 | **−20.5 %** (previously +14 %) |
| batch | 1,792,125 | 1,784,222 | −0.4 % | 1,783,004 | 2,009,527 | −11.2 % |
| iot | 916,584 | 916,719 | +0.01 % | 914,616 | 972,913 | −5.8 % |
| queue | 2,074,290 | 2,074,367 | +0.00 % | 2,011,999 | 2,707,950 | −23.4 % (C: −25.7 %) |
| microservices | 3,499,350 | 3,499,176 | −0.00 % | 3,491,659 | 3,873,242 | −9.7 % |
| high-cardinality | 11,116,228 | 11,116,306 | +0.00 % | 11,110,319 | 11,274,842 | −1.4 % |
| adversarial-random | 23,669,579 | 23,669,584 | +0.00 % | 23,869,534 | 21,676,199 | **+9.2 % (worse)** |

- Cost of the catalog copy: ≤ +1.1 % on uncompressed variants (maximum `inventory/llog-contextual`), ≈ 0 % with
  compression.
- The `inventory` gain does not come from the copy but from compressing catalog blocks (they were never compressed
  before, and that store has one context per operation).
- **Overall result**: Lattice-S+deflate is smaller than JSONL+zstd-3 on **7 of 8** workloads (−1.4 % to −23.4 %;
  Lattice-C+deflate up to −25.7 %). The aspirational −30 % target is still not met, and zstd-19 is still smaller on every
  workload.
- Robustness: a flipped bit in a schema block goes from losing 27/27 records (0.1) to 0/27 (0.2), verified by both
  readers on the golden files.
