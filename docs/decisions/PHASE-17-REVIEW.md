# PHASE-17-REVIEW — Query server, catalog and multi-tenancy (Server v1.0)

- Status: **CONDITIONAL** (single-node server with namespaces, roles, a complete and tested read API, and online
  administration; missing a built Docker image, TLS between machines and validation on other hardware)
- Date: 2026-11-18
- Deliverables: `src/LatticeLog.Server` (`LatticeServer`), `src/LatticeLog.ServerHost` (`latticelog-server`),
  `specs/server-api.md`, `deploy/Dockerfile`, `docs/deploy/single-node.md`, `tests/LatticeLog.Tests/ServerApiTests.cs`,
  `ConcurrentReadTests.cs`, `reports/ingest-v1.md` §3–§4

| task | status | evidence |
|---|---|---|
| T01 tenant/project/environment | done | namespaces `tenant[/project[/env]]`, one store per namespace; isolation tested |
| T02 separate, transactional metadata | partial | per namespace: STORE.json + MANIFEST.jsonl (journal with CRC and fsync) + footers; no global control plane |
| T03 catalog | partial | `GET /v1/catalog` (segments, ranges, services, manifest problems) and `/v1/schema`; no online compaction state |
| T04 endpoints | done | ingest, search, event, related, trace, stats, overview, error groups, context pack, schema, export, catalog |
| T05 authn/authz | done | tokens with `ingest`/`read`/`admin` scopes and namespaces; 401/403 tested |
| T06 quotas | partial | ingestion: records/s per token; reads: per-token and global concurrency, timeout, scanned records; no per-tenant byte quotas |
| T07 signed cursors | done | HMAC-SHA256 + expiry, bound to namespace and token; tampering, borrowing and expiry tested |
| T08 reads separated from ingestion | done | query semaphore on the thread pool vs dedicated writer threads; CPU competition measured (§4) |
| T09 snapshots and compaction | documented | per-request snapshot; cursors by stable sequence; compaction retires segments through the manifest |
| T10 recent and compacted data | done | a single planner over sealed segments and the active one |
| T11 arrival vs event time | done | `observed_time` = arrival at the server unless the producer sets it; documented and tested |
| T12 administration (retention, compaction, reindexing) | done | `POST /v1/admin/retention\|compact\|verify\|erase\|backup` without stopping ingestion, audited (`ServerApiTests.Online_retention_compaction_and_verify_keep_dedup_and_are_audited`); reindexing is not needed (per-segment indexes) |
| T13 metadata migrations | not applicable yet | a single v1 metadata format; the STORE.json version is checked on open |
| T14 Docker and health | partial | `deploy/Dockerfile` (chiseled, non-root, configuration through env and secrets) **not built**; `/v1/health` with admin details |
| T15 single-machine guide | done | `docs/deploy/single-node.md` |
| T16 producers + readers load | done on this machine | `reports/ingest-v1.md` §4 (4 CPUs because of the machine's instability) |

- Server v1.0 gate ("multiple sources ingest and query safely, with recoverable persistence and without requiring a
  cluster"): **met on loopback**. Missing: tested TLS, a real network, the built image and repeating the load on stable
  hardware.
- **Platform finding**: the reference machine (i9-14900KF, microcode 0x11D, 2023 BIOS) corrupts memory under load on
  many cores, also in a control program without LatticeLog (`tools/platform-stress`). Until the BIOS is updated,
  concurrency measurements are taken with a limited CPU set and do not generalize.
- Decision: proceed with the listed open items. Repeat on another machine or in CI before making performance claims.
