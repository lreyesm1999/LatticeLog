# LatticeLog lab (P02)

Goal: be able to refute or confirm any claimed improvement with reproducible experiments.

| piece | location |
|---|---|
| Deterministic generator of 8 workloads (HTTP/API, inventory, batch, IoT, queues, microservices, high cardinality, adversarial) with incident *ground truth* | `LatticeLog.Lab/Workloads.cs` |
| Baselines: text, JSONL, JSONL+gzip, JSONL+zstd-3/19, JSONL+LZ4; `.llog` raw/S/C ± DEFLATE | `LatticeLog.Lab/Program.cs` |
| Round-trip verification by SHA-256 of the canonical JSONL | same (`roundtrip ok`) |
| Fixed queries (trace, errors, event, 1 % window): indexed vs full scan vs JSONL+zstd | same |
| Preliminary Lattice-P study (adaptive code lengths) | same (`Entropy`) |
| Public dataset experiment (Loghub-2.0, read straight from the zips) | same (`loghub`), analysis in `../reports/loghub2-v1.md` |
| Results | `results/` (CSV + Markdown per run) and analysis in `../reports/baseline-v1.md` |

Usage:
```
dotnet run -c Release --project research/LatticeLog.Lab -- baseline --events 100000 --reps 3
dotnet run -c Release --project research/LatticeLog.Lab -- generate --workload queue --events 1000000 --out queue.llog.jsonl
```

Open: real anonymized datasets; 10M/100M sizes; a variable-rate replayer with producer latency; thermal control,
confidence intervals and cold cache; external AI-friendly evaluation (P02-T17, P12-T17), which needs an external model
and human review outside this repository.
