# PHASE-07-REVIEW — Lattice-S: structural encoding (R&D)

- Status: **PASS**
- Date and version: 2026-11-18 · 0.1.0-alpha.1
- PRs and issues closed: none (tasks are tracked by ID in docs/MASTER-PLAN.md)
- Verified deliverables: CodecId.Structural (per-block dictionary, deltas, schema-shaped attributes)
- Test evidence: properties + golden files
- Benchmark evidence: S vs raw: −40 % to −64 % on repetitive workloads; ≈0 on high cardinality; S+deflate vs zstd-3: format 0.1 −1 % to −23 % on 6/8, worse on 2/8; format 0.2 (compressed catalogs) −1.4 % to −23.4 % on 7/8, worse only on the adversarial one (+9.2 %)
- Security/compatibility: n/a
- Hypotheses accepted/refuted: G7: reversible, reproducible, and clear about when it wins and when it does not
- Open limitations: T06 columnar/hybrid layout not evaluated
- New ADRs: see docs/decisions/ADR-*.md
- Residual risks: R03
- Decision: keep S as the default compaction codec
