# Operating `latticelog-server` (P24)

Status on 2026-11-18: a single node (plus an optional replica). Complements `docs/deploy/single-node.md`.

## SLIs and initial targets (P24-T01)

Starting targets for one node; they have to be revisited with real data and on stable hardware.

| SLI | measure | initial target |
|---|---|---|
| Ingestion availability | % of `POST /v1/ingest` answered with 2xx or 409, excluding quota 429s | 99.9 % monthly |
| ACK latency | p99 of the `fsync` ACK per 5,000-record batch | < 250 ms |
| Durability | acknowledged batches that are unreadable after a crash | 0 |
| Search | p95 of `/v1/search` over the last 24 h, ≤ 100 results | < 2 s |
| Replica lag | `latticelog_replication_lag_records` | 0 in steady state; < 60 s of data after a spike |
| Restore | time to restore a verified backup | < 30 min per 100 GB (not measured yet) |

## Metrics and alerts (P24-T02..T04)

Scrape `/metrics` with an admin token (`Authorization: Bearer …`) every 15–30 s. The server does not write its own logs
into its stores, so there are no feedback loops.

| alert | condition | action |
|---|---|---|
| Failed namespace | `latticelog_ingest_failed == 1` | The namespace refuses writes after a disk error. See *Disk full or I/O error*. |
| Saturated queue | `latticelog_ingest_queue_depth` > 80 % of capacity for 5 min | Check CPU and disk; lower `MaxConcurrentQueries` if queries compete; spread namespaces. |
| Ingestion stopped | `rate(latticelog_ingest_records_stored_total[10m]) == 0` with known producers | Check the network and tokens (401s show up in the audit log as `auth.*`). |
| Replica behind | lag > 0 for 5 min and growing | Check the status in `/v1/health`; check the replica and the replica token. |
| Audit losing events | `increase(latticelog_audit_events_dropped_total[5m]) > 0` | Slow or full disk under `data/audit`. |
| Disk | free space < 15 % (system exporter) | Retention (`/v1/admin/retention`) or a larger disk. The writer refuses to write below `MinFreeDiskBytes`. |
| Brute force | many `auth.*` entries with `unauthorized` in the audit log | The brake (429 per IP) is already acting; investigate the source. |

## Backup and restore (P24-T05, T06)

- **Hot**: `POST /v1/admin/backup?ns=<namespace>` (requires `--backup-dir`) or, on the machine,
  `latticelog backup <data>/ns/<dir> <destination>`. Sealed segments are copied as they are; the active one up to its
  last complete block. The copy is verified by reading it (`verified: true`). Whatever is written after the backup is
  not in it.
- Also keep `server.key` (cursor signing; without it, issued cursors stop being valid, nothing more) and the tokens
  file.
- **Restore**: stop the server → copy the backup directory to `data/ns/<dir>` → start. Recovery seals the copied active
  segment and rebuilds deduplication from the data, so producers resend whatever came after the backup without
  duplicates (if they still have it in their spool).
- **Drill**: periodically restore on another machine and run `latticelog verify`. Tested in `BackupTests` (copy while
  the writer keeps writing, restore and write afterwards), and drilled on every CI run: a backup taken on Linux x64 is
  restored on Windows x64, macOS ARM64 and Linux ARM64, verified, compared record by record and written to
  (`backup-drill-*` jobs in `.github/workflows/ci.yml`).
- **RPO/RTO**: without a replica, the RPO is the interval between backups, except for what is still in the producers'
  spools. With a replica, the RPO is the replica lag at the moment of failure. The RTO depends on size and is not
  measured.

## Runbooks (P24-T09)

### Disk full or I/O error
1. `latticelog_ingest_failed == 1` or 503s on ingestion. Producers keep data in their spool.
2. Free space: a retention dry run (`dry_run=true`) to see what would be deleted, then the real run. Do not delete files
   by hand.
3. Restart the server: recovery checks every namespace and the namespace accepts writes again.

### Corruption
1. `POST /v1/admin/verify?ns=…` or `latticelog verify`. Queries already report `storage_issues` in their coverage.
2. A segment with corrupt blocks is still read up to the damage; it is not repaired in place. Restore that segment from
   a backup or the replica with the server stopped.

### Server down
1. Start it again; recovery is automatic (it seals the active segment and rebuilds deduplication).
2. If it does not start: check the diagnostic log on stderr. With a replica, follow the failover in
   `docs/operations/replication.md`.

### Compromised token
1. Remove it from the tokens file (reloaded without a restart). Issue a new one.
2. Search the audit log for that `token`'s actions since the suspected date.
3. If it was an admin token or had broad read access: rotate `server.key` (invalidates cursors) and assess whether
   there is an obligation to notify.

### Privacy: erasure request
1. `POST /v1/admin/erase?ns=…&where=…&dry_run=true` to count; then without `dry_run`.
2. Repeat on the replica. Earlier backups **still contain** the data: either they are deleted when they expire, or they
   are restored, erased and backed up again. Producer spools expire with their local retention.
3. The audit log keeps the filter used, which may contain the personal data. Handle it accordingly.

### Upgrade and rollback (P24-T10, T11)
1. Back up every namespace. Stop, replace the binary or image, and start.
2. Rollback: the `.llog` format of the same major is readable in both directions (`docs/compatibility.md`). A segment
   written by the new version with a new optional feature is read sequentially by the previous one. If the release
   notes say otherwise, restore the backup.

## Capacity (P24-T16)

Measured only on the reference machine with 4 CPUs (`reports/ingest-v1.md`): with the binary body, 470k–561k records/s
with 1 producer, 1.15M with 4 and 1.45M with 16. With concurrent readers, ingestion drops to roughly half. These figures
are **not a sizing guide** until they are repeated on stable hardware.

## Open

Ready-to-import dashboards (T03), canary/rolling deployments (T10), drills (T12), costs (T13), on-call and
communication (T14), Kubernetes (T15), regional DR (T08).
