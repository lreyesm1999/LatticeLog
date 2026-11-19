# PHASE-24-REVIEW — Operations, SRE, backup and DR

- Status: **CONDITIONAL** (metrics, verified backup, initial SLIs and runbooks written; no drills on real
  infrastructure)
- Date: 2026-11-18
- Deliverables: `GET /metrics` and `POST /v1/admin/backup` (`LatticeServer.Ops.cs`), `src/LatticeLog.Storage/Backup.cs`,
  `latticelog backup`, the `--backup-dir`, `--metrics-anonymous` and `--replicate-to` options of `latticelog-server`,
  `docs/operations/runbook.md`, `tests/LatticeLog.Tests/BackupTests.cs`

| task | status | evidence |
|---|---|---|
| T01 SLI/SLO | done (initial) | runbook §SLIs; not measured in production |
| T02 metrics without loops | done | Prometheus per namespace; the server never writes into its own stores |
| T03 dashboards | not done | — |
| T04 alerts | done (defined) | runbook §Metrics and alerts |
| T05 consistent backup | done | hot, valid prefix of the active segment, verified by reading the copy |
| T06 restore on new infrastructure | done (CI) | every CI run backs up a store on Linux x64 and restores it on Windows x64, macOS ARM64 and Linux ARM64: verified, identical record by record to the source, and accepting new writes (`backup-drill-*` jobs); not yet on the user's own servers |
| T07 RPO/RTO | partial | RPO described; RTO not measured |
| T08 regional DR | not done | — |
| T09 runbooks | done | full disk, corruption, crash, compromised token, privacy, upgrade |
| T10–T12 canary, rollback, drills | partial | rollback described from format compatibility; no drills |
| T13 costs | not done | — |
| T14 on-call | not applicable | no team |
| T15 on-prem/Docker/K8s | partial | binary and a Docker image built, scanned and smoke-tested in CI; no Kubernetes |
| T16 capacity | partial | figures with 4 CPUs, not generalizable |

- Also: a middleware turns any uncaught exception into a JSON 500 and reports it through diagnostics.
- Gate G24 ("restore tested, SLOs observable, runbooks executed"): a restore on other machines runs in every CI build; the
  runbooks still have to be executed on real infrastructure.
