# Roadmap

## In plain words (2026-11-21)

| Now | Next | Later | Out of scope |
|---|---|---|---|
| Local engine, CLI, MCP server, per-user service, web console, Python/Rust/Node.js/PHP SDKs: usable, alpha. Closing Local v1.0 (P14) | Measurements on stable hardware and Linux; server over a real network; encryption at rest and an external pentest; runbook drills; external evaluation of the AI-facing evidence | Cluster, object-storage tiers, Java/Go SDKs, a fuller console with user testing, published packages and a stable release | Running AI models inside LatticeLog |

What blocks a stable 1.0 is listed per phase below: mainly P14 (signing, N-1 compatibility, a release candidate) and
measurements on other hardware. Suggestions and votes: open an issue.

## Phase status

Updated 2026-11-18. This is the source of truth for the status of each phase; details are in
`docs/decisions/PHASE-XX-REVIEW.md`.
Statuses: PASS · CONDITIONAL (gate met with explicit open items) · NOT PASSED · NOT STARTED · N/A.

| phase | title | status |
|---|---|---|
| P00 | Vision, scope, requirements and governance | CONDITIONAL |
| P01 | Semantic event model and contracts | PASS |
| P02 | Lab, datasets and baselines (R&D) | CONDITIONAL |
| P03 | .NET SDK and event generator | CONDITIONAL |
| P04 | `.llog` v0.x binary specification | PASS |
| P05 | Ingestion pipeline, WAL and durable writes | CONDITIONAL |
| P06 | Reader, exact reconstruction and compatibility | PASS |
| P07 | Lattice-S: structural encoding (R&D) | PASS |
| P08 | Lattice-C: context inheritance (R&D) | CONDITIONAL |
| P09 | Lattice-P: prediction (R&D) | PASS (negative result) |
| P10 | Adaptive planner and compaction | CONDITIONAL |
| P11 | Indexes, queries and selective retrieval | PASS |
| P12 | AI-friendly reading without AI: evidence | CONDITIONAL |
| P13 | OpenTelemetry interoperability and importers | CONDITIONAL |
| P14 | Production-ready .NET SDK (Local v1.0) | NOT PASSED |
| P15 | CLI and local tools | CONDITIONAL |
| P16 | Remote ingestion protocol and collector | CONDITIONAL (binary body, multi-producer, TLS tested end to end; real network between machines pending) |
| P17 | Query server and multi-tenancy | CONDITIONAL (online admin, retention, Docker image built and smoke-tested in CI; other hardware pending) |
| P18 | Security, privacy and auditability | NOT PASSED (audit, erasure, brake, headers; no encryption at rest or pentest) |
| P19 | Horizontal scalability and replication | NOT PASSED (async replica + manual failover; no cluster) |
| P20 | Tiered storage and object storage | NOT STARTED |
| P21 | SDKs in other languages | CONDITIONAL (Python, Rust, TypeScript/Node.js and PHP; Java/Go out of scope for now) |
| P22 | Read-only MCP adapter | CONDITIONAL |
| P23 | Web console | CONDITIONAL (minimal, secure console; no user testing or browser E2E) |
| P24 | Operations, SRE, backup and DR | CONDITIONAL (metrics, verified backup, runbooks; no real drills) |
| P25 | Commercial product, licensing and SaaS | N/A (decision: free project under the MIT license; `docs/product/commercial-decisions.md`) |
| P26 | Quality, chaos and internal certification | NOT PASSED (practices in place; stable hardware missing) |
| P27 | Launch, documentation and support | CONDITIONAL (public repository under MIT; packages not published) |
| P28 | Maintenance and evolution | NOT STARTED (policy drafted) |

## Current
- Close the M1/M3 items for Local v1.0 (P14). Done on 2026-11-18: real-process crash tests, ring and pool on the
  enabled path (56 ns / 5 B), trimming/AOT analysis active and clean, local NuGet packages, fixed-rate latency
  benchmark. Open: repeating the latency measurement on another disk and on Linux (the stall cause is
  identified and mitigated), signing, N-1 and RC (`docs/decisions/PHASE-14-REVIEW.md`).
- ~~Mitigate P04-R1~~ done in format 0.2 (compressed catalog copy in the footer).

## Next
- P16–P17 open items: a real network between machines; publishing the image to a registry.
- P18: encryption at rest, external pentest (`docs/security/pentest-scope.md`).
- P24: run the runbooks on real infrastructure (restoring on another OS and CPU architecture already runs in CI).
- **Platform**: the reference machine corrupts memory under all-core load (old BIOS/microcode); repeat the measurements
  on stable hardware.
- External AI-friendly evaluation (P12-T17..T19) with an external model and human review.
- Real datasets of our own in the lab (Loghub-2.0 already measured: `reports/loghub2-v1.md`).

## Later
- P19 cluster (after measuring single-node limits on stable hardware), P20 tiers/object storage, Java/Go/Node SDKs,
  P23 full console with user testing, P27 packages and stable release.

## Blocked (needs people or resources)
- User interviews (P23-T01), pentest (P18-T16).
