# PHASE-19-REVIEW — Horizontal scalability and replication

- Status: **NOT PASSED** (asynchronous replica and manual failover done; there is no cluster)
- Date: 2026-11-18
- Deliverables: `src/LatticeLog.Server/Replication.cs`, upstream provenance tracking in `IngestService`,
  `--replicate-to` in `latticelog-server`, `tests/LatticeLog.Tests/ReplicationTests.cs`,
  `docs/operations/replication.md`

| task | status | evidence |
|---|---|---|
| T01 single-node limits | partial | `reports/ingest-v1.md` with 4 CPUs; the machine is not stable enough to measure the real limit |
| T02–T04 shards, router, control plane | not done | — |
| T05 leader/epoch/fencing | not done | documented as a risk: nothing prevents two primaries at once |
| T06 consistency | documented | asynchronous: the ACK does not wait for the replica |
| T07 replication | done (asynchronous) | one `Forwarder` per namespace; persistent cursor; lag in `/metrics` and `/v1/health` |
| T08 duplicates after failover | done | the replica learns the original producer's sequences from provenance and keeps them across restarts; tested: nothing duplicated or lost when resending to the replica |
| T09 snapshots and rebuild | partial | verified hot backup (P24); indexes and catalog are rebuilt from segments (P11) |
| T10–T12 shard movement, per-node quotas | not done | — |
| T13 partial results | done on one node | `partial_results` and `storage_issues` in the coverage |
| T14 partitions, split-brain | not done | only a primary crash was tested |
| T15 scaling 1/3/5/N | not done | — |
| T16 topologies and runbook | done for primary + replica | `docs/operations/replication.md` |

- Gate G19 ("no node failure leads to false confirmations…; horizontal scaling measured"): the false-confirmation part
  is met for primary + replica (the ACK never claims replication); scaling does not exist.
- Decision: do not build a cluster before measuring single-node limits on stable hardware (T01).
