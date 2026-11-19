# PHASE-02-REVIEW — Lab, datasets and baselines (R&D)

- Status: **CONDITIONAL**
- Date and version: 2026-11-18 · 0.1.0-alpha.1
- PRs and issues closed: none (tasks are tracked by ID in docs/MASTER-PLAN.md)
- Verified deliverables: research/LatticeLog.Lab (8 deterministic workloads, JSONL/gzip/zstd/LZ4 baselines), scripts/reproduce.*, reports/baseline-v1.md
- Test evidence: SHA-256 round trip on every row; queries consistent across 3 methods
- Benchmark evidence: 100k events × 8 workloads × 3 reps, one machine
- Security/compatibility: no sensitive data in the repository
- Hypotheses accepted/refuted: baselines measured and reproducible on the same machine
- Update 2026-11-18: T01 done with Loghub-2.0 (14 datasets, `reports/loghub2-v1.md`): byte-exact text import verified but 1.5–2.7× larger than zstd-3; typed model −25 % vs CSV+zstd-3 (14/14), +17 % vs CSV+zstd-19
- Open limitations: T02 no real data of our own; T07 10M/100M scale; T08 variable-rate replayer; T10 CLP; T15 confidence intervals/thermal control; T17 external AI evaluation; G2 requires a run on another machine
- New ADRs: see docs/decisions/ADR-*.md
- Residual risks: R14, R20
- Decision: proceed; repeat on Linux/a second machine before public claims
