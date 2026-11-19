# Loghub-2.0 — first measurement with public data (P02-T01)

Date: 2026-11-18 · `dotnet run -c Release --project research/LatticeLog.Lab -- loghub --lines 200000` · Raw data:
`research/results/loghub2-20261118-104533.csv` · Same machine as `reports/baseline-v1.md`.

**Data**: Loghub-2.0 (Zenodo 8275861, CC-BY-4.0; Jiang et al., ISSTA 2024), 14 datasets, MD5 verified. The **first
200,000 lines** of each dataset are used (or all of them when there are fewer), read straight from the zips without
extracting. It is not a random sample: the beginning of each file may not represent the rest.

## What is compared

1. **Byte-exact text**: every raw line goes through `RawTextImporter` and is stored as `.llog` (Lattice-S + DEFLATE).
   Exporting it is checked to return exactly the same bytes (**14/14 identical**). It is compared with the raw text
   compressed with zstd-3 and zstd-19.
2. **Typed**: every line of Loghub's structured CSV (`*_full.log_structured.csv`) becomes a typed event. The template
   (`EventId`) is the event; the header columns and the values of the `<*>` wildcards are the attributes; integers
   without leading zeros become Int64. Every `Content` is checked to be rebuilt exactly from its template and parameters
   (**0 mismatches and 0 templates that do not fit**) and the `.llog` is checked to read back identically. It is
   compared with the same CSV compressed with zstd, because it holds the same information. It is not compared with the
   raw text as if it were equivalent: the exact layout of the original line (spaces, header separators) is not part of
   this variant.

The templates are annotated by Loghub itself (ground truth). This approximates an application that emits typed events
at the source; it does not measure a log parser.

## Results (MB = 10⁶ bytes)

| dataset | lines | text | zstd-3 | zstd-19 | llog text | vs zstd-3 | CSV+zstd-3 | CSV+zstd-19 | llog typed | vs CSV+zstd-3 | vs CSV+zstd-19 | templates |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Apache | 51,978 | 4.98 | 0.26 | 0.17 | 0.48 | +82 % | 0.30 | 0.20 | 0.17 | **−44 %** | −16 % | 29 |
| BGL | 200,000 | 26.62 | 3.03 | 1.43 | 4.72 | +56 % | 0.89 | 0.53 | 0.54 | **−40 %** | +1 % | 118 |
| HDFS | 200,000 | 28.01 | 2.56 | 1.67 | 4.69 | +83 % | 2.65 | 1.76 | 2.52 | −5 % | +43 % | 22 |
| HPC | 200,000 | 14.06 | 1.49 | 0.88 | 2.52 | +69 % | 0.53 | 0.38 | 0.27 | **−49 %** | −29 % | 47 |
| Hadoop | 179,993 | 31.89 | 1.81 | 1.08 | 3.82 | +111 % | 1.34 | 0.80 | 1.02 | −24 % | +28 % | 236 |
| HealthApp | 200,000 | 19.30 | 1.94 | 1.18 | 3.32 | +71 % | 1.69 | 0.92 | 1.21 | −28 % | +31 % | 156 |
| Linux | 23,921 | 2.14 | 0.18 | 0.11 | 0.33 | +85 % | 0.15 | 0.10 | 0.11 | −23 % | +19 % | 338 |
| Mac | 100,314 | 15.44 | 0.87 | 0.57 | 2.38 | +173 % | 0.96 | 0.63 | 0.79 | −18 % | +25 % | 626 |
| OpenSSH | 200,000 | 21.66 | 1.50 | 0.82 | 2.28 | +53 % | 1.00 | 0.62 | 0.42 | **−58 %** | −32 % | 34 |
| OpenStack | 200,000 | 59.20 | 4.98 | 3.74 | 8.97 | +80 % | 2.03 | 1.34 | 1.53 | −25 % | +14 % | 48 |
| Proxifier | 21,320 | 2.52 | 0.18 | 0.12 | 0.32 | +74 % | 0.21 | 0.14 | 0.15 | −29 % | +6 % | 11 |
| Spark | 200,000 | 24.15 | 1.32 | 0.77 | 2.41 | +83 % | 1.73 | 1.05 | 1.34 | −22 % | +28 % | 162 |
| Thunderbird | 200,000 | 29.80 | 2.03 | 1.38 | 4.31 | +113 % | 1.12 | 0.80 | 0.85 | −24 % | +6 % | 588 |
| Zookeeper | 74,273 | 10.33 | 0.45 | 0.27 | 0.82 | +84 % | 0.31 | 0.22 | 0.19 | **−39 %** | −13 % | 89 |
| **total** | | 290.09 | 22.60 | 14.19 | 41.38 | **+83 %** | 14.92 | 9.49 | 11.12 | **−25 %** | **+17 %** | |

## An honest reading

- **Negative result: storing raw text in `.llog` does not compress well.** The byte-exact import takes **1.5–2.7× the
  size of the text with zstd-3** (+83 % in total). Bodies are opaque bytes to the codec (no block dictionary), and
  DEFLATE, with its 32 KiB window, falls far behind zstd. This import is for keeping the exact original and being able
  to query it, not for saving space. Possible improvements, not measured yet: zstd as block compression (a format
  change, needs an ADR) and separating parsed headers from the rest of the line.
- **Positive result: the typed model is smaller than the same content as CSV+zstd-3 on all 14 datasets** (−5 % to
  −58 %, −25 % in total). On 5 of 14 (Apache, BGL, HPC, OpenSSH, Zookeeper) it beats the aspirational −30 % target.
  Against CSV+zstd-19, the slow offline compressor, it only wins on 4 of 14 (+17 % in total).
- The typed case is the product's design scenario (events with a contract), but here it starts from perfect templates
  annotated by Loghub. With automatically extracted templates or poorly designed events, the result will be worse.
- Datasets with many high-cardinality parameters (HDFS: block IDs and IP addresses) gain the least (−5 %), like the
  synthetic `high-cardinality` workload (§1 of `baseline-v1.md`).
- Times were not measured in this experiment (size only). One machine and a single run (the output is deterministic for
  the same zips).

To reproduce: download Loghub-2.0 (see `research/dataset-manifest.yaml`) and run the command above; the output CSV
should match the published one byte for byte if the zstd/DEFLATE versions are the same.
