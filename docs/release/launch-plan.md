# Launch plan (P27)

Status on 2026-11-18. The order is fixed: no package is published before the blocking boxes in
`docs/release/rc-checklist.md` are checked.

## Phase 0 — before the RC
- ~~License applied~~ done: MIT. Name checked (no other logging project called LatticeLog on GitHub, 2026-11-18).
- ~~Public repository~~ done, with CI on Linux/macOS/Windows + AOT, SBOM, private vulnerability reporting,
  `CONTRIBUTING.md`, `CODE_OF_CONDUCT.md`, issue forms and a PR template.
- Open: branch protection with required reviews on `main` (P27-T10).

## Phase 1 — public RC (P27-T13)
- Packages: NuGet (`LatticeLog`, `LatticeLog.Ingest`, …), PyPI (`latticelog`), Packagist (`latticelog/latticelog`) and
  the server image. All `0.x.0-rc.N`.
- Minimal published documentation (P27-T02): README, `.llog` format, ingest protocol, server API, query language,
  CLI, MCP, deployment, operations, compatibility.
- Migration guides (P27-T03): from `ILogger`/Serilog (the `LatticeLog.Extensions.Logging` bridge exists), from OTLP
  (the importer exists), Python `logging` (the handler exists), PHP (PSR-3 adapter pending).
- Diagnostic demo (P27-T05): search vs Context Pack on Loghub-2.0, with its limits.
- Published benchmarks (P27-T06) **only** with hardware, versions, datasets and scripts, repeated on a stable machine.

## Phase 2 — stable (P27-T14)
- At least 2 weeks of RC with no open blockers (corruption, cross-tenant leak, false ACK, unrecoverable crash:
  P26-T16).
- Release notes and an upgrade guide; a launch-day rollback plan (`docs/operations/runbook.md`).

## Post-launch metrics (P27-T15)
Activation (first event received), ingested records, queries, 30-day developer retention and onboarding failures.
They require **opt-in, declared** telemetry; the product sends nothing by default.

## Landing page (P27-T12)
Only claims backed by `reports/`: measured compression ratios, fsync-confirmed ACKs, lossless reads and verifiable
evidence. No "N times faster" without the machine and the dataset next to it.
