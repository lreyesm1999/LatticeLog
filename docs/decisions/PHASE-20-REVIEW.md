# PHASE-20-REVIEW — Tiered storage and object storage

- Status: **NOT STARTED**
- Date: 2026-11-18
- Available foundation: segments immutable once sealed, a journaled manifest, retention by age and size
  (`Retention.cs`), verified backup. That is the foundation for `hot`/`warm`; there is no `cold` tier or object
  storage.
- Decision: depends on P19-T01 and on real demand for long retention.
