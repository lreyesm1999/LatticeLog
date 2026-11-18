# Server API v1 (P17)

`latticelog-server` (`src/LatticeLog.ServerHost`, library `src/LatticeLog.Server`). Ingestion is specified in
`specs/ingest-protocol.md`; this document covers namespaces, tokens and the read API. All responses are JSON unless
stated; errors are `{"error": "<code>", "message": "…"}`.

## 1. Namespaces

A namespace is `tenant[/project[/environment]]`; each segment matches `[a-z0-9][a-z0-9._-]{0,62}`. Each namespace is
an independent store at `<data>/ns/<tenant~project~env>`:
- **Separate data:** sequence numbers, dedup state, indexes and segments.
- **One namespace per query:** a query never reads two namespaces.
- **Creation:** a namespace exists after its first accepted batch. Reading one with no data gives 404
  `namespace_not_found`.
- **Selecting it:**
  - Ingestion uses the header `LatticeLog-Namespace`, or the token's first namespace.
  - Reads use the query parameter `ns`, or the token's first namespace.

## 2. Tokens and scopes

`tokens.json` stores only SHA-256 hashes (`latticelog-server hash-token <secret>`). The server re-reads it when it
changes, so tokens rotate without a restart.

```json
{"tokens":[
  {"id":"orders-api","sha256":"…","scopes":["ingest"],"namespaces":["acme/prod"],"records_per_second":5000},
  {"id":"sre","sha256":"…","scopes":["read"],"namespaces":["acme/prod","acme/staging"]},
  {"id":"ops","sha256":"…","scopes":["read","admin"],"namespaces":["*"]}]}
```

| scope | allows |
|---|---|
| `ingest` | `POST /v1/ingest` into its namespaces (optionally restricted to `producers`, rate limited by `records_per_second`) |
| `read` | every `GET` below on its namespaces |
| `admin` | per-namespace details in `/v1/health` |

Defaults: scopes `["ingest"]`, namespaces `["default"]`. Missing or unknown token → 401. A token without the scope →
403 `forbidden_scope`. A namespace the token does not have → 403 `forbidden_namespace`.

## 3. Read endpoints

Common parameters:
- `ns`: the namespace;
- `where`: the query language (`specs/query-language.md`);
- `since` / `until`: RFC 3339, or a relative duration such as `15m`, `2h` or `7d`;
- `limit`: default 100, maximum `MaxPageSize`, which is 1000 by default;
- `order`: `asc` or `desc`;
- `cursor`: a signed cursor;
- `timeout_ms`: default 10 s, maximum 60 s.

| endpoint | answer |
|---|---|
| `GET /v1/namespaces` | namespaces with data that the token can read |
| `GET /v1/search` | `latticelog.query.v1`: `coverage` (returned, truncated, partial_results, partial_reason, includes_unsealed_segments, next_cursor), `stats`, `events` (secrets redacted unless `redact=false`; `explain=true` adds the plan) |
| `GET /v1/events/{seq or llog-ref}` | evidence for one event (404 `event_not_found`) |
| `GET /v1/events/{ref}/related` | explicitly related events (same trace, operation or correlation) |
| `GET /v1/traces/{trace_id}` | trace timeline (`budget_bytes`) |
| `GET /v1/stats` | `latticelog.stats.v1`: counts `by=event|level|service|time|<field>`, `bucket=5m`, `top` |
| `GET /v1/overview`, `/v1/error-groups`, `/v1/context`, `/v1/schema` | evidence documents `latticelog.evidence.v1` (`budget_bytes` for context packs); headers `LatticeLog-Truncated`, `LatticeLog-Partial` |
| `GET /v1/export` | one lossless page of `latticelog.jsonl` (≤ 10,000 records), with headers `LatticeLog-Next-Cursor` (signed), `LatticeLog-Truncated`, `LatticeLog-Partial` and `LatticeLog-Records` |
| `GET /v1/catalog` | `latticelog.catalog.v1`: segments (sealed or active, records, sequence and time ranges, services, bytes) and manifest problems |
| `GET /v1/health` | `ok`/`failed` plus the number of open namespaces; with an `admin` token, also per-namespace store id, last sequence, producers, batches, flushes and queue depth |

## 4. Semantics

- **Cursors (P17-T07).** `c1.<expiry>.<engine cursor>.<HMAC-SHA256>`. The key lives in `<data>/server.key` (32
  random bytes, created at first start). The MAC covers the namespace, the token id, the expiry and the engine cursor.
  A tampered cursor, an expired one (`CursorTtl`, default 1 h) or one used by another token or namespace → 400
  `invalid_request`.
- **Snapshots (P17-T09).** A request opens the store once. The segment list it reads is fixed at that moment. Records
  written later are not mixed in, except into the active segment, which is read up to its last complete block
  (`includes_unsealed_segments`). Pages of the same cursor follow sequence order, so they never repeat or skip
  records that existed when the first page was read. Compaction (offline today) retires segments through the manifest
  and never rewrites a segment in place.
- **Recent and old data together (P17-T10).** One planner reads sealed segments (pruned by their footer indexes) and
  the active segment.
- **Timestamps (P17-T11).** `time` is the event time chosen by the producer. `observed_time` is when the collector
  received the record, unless the producer had already set it. Queries filter on `time`.
- **Limits (P17-T06/T08).**
  - Concurrency: `MaxConcurrentQueries` (default half the CPUs, at least 2; otherwise 503 `overloaded` after
    `QueueWait`) and `PerTokenConcurrentQueries` (default 4; otherwise 429 `too_many_queries`).
  - Per request: `MaxScannedRecords` (default 20 M). Past it the answer is returned with `partial_results=true`.
  - Separation: reads run on the thread pool under their own semaphore, while ingestion uses one dedicated writer
    thread per namespace. A slow query never delays an acknowledgement through shared locks; it only competes for CPU
    (`reports/ingest-v1.md` §4).
- **Errors.**
  - 400: `invalid_query`, `invalid_request`, `invalid_namespace`;
  - 404: `namespace_not_found`, `event_not_found`;
  - 503: `query_timeout`;
  - 500: `internal_error` (reported through `ServerOptions.Diagnostics`; the stack trace is never sent to the client).

## 5. Administration (scope `admin`, P17-T12, P18, P24)

Every admin call takes `ns=<namespace>`, runs **while the server keeps ingesting and answering reads**, and is
written to the audit log. Requests are serialized per namespace (one admin operation at a time).

| endpoint | parameters | effect |
|---|---|---|
| `POST /v1/admin/retention` | `max_age` (`30d`, `12h`…) and/or `max_mb`; `dry_run=true` | deletes whole **sealed** segments, oldest first, entirely older than the cutoff / until under the size. The deletion is journaled (`segment_deleted`) before the file goes. Never touches the active segment. |
| `POST /v1/admin/compact` | `policy=fast\|balanced\|compact` | rewrites sealed segments into fewer, larger ones; sequences and references stay valid. 409 `compaction_refused` when there is nothing to do or no space. |
| `POST /v1/admin/verify` | — | full read of the namespace; returns records and issues. Uses a query slot (503 `overloaded` when none). |
| `POST /v1/admin/erase` | `where=<filter>` (required, must not match everything); `dry_run=true` | privacy erasure (P18-T12): rewrites every segment that holds a match without those records; the active segment is sealed first. The rewritten files contain no trace of the erased values. Resends of the erased batches are answered as duplicates, so they do not come back. |
| `POST /v1/admin/backup` | — | hot backup into the server's configured backup directory (`--backup-dir`; the client never names a path): sealed segments as they are, the active one up to its last complete block, verified by reading it back. 409 `backup_not_configured` without a directory. |

Erasure and backups: an erase does not reach backups taken before it, nor the producers' local spools, nor a replica
(the replica must receive the same call). This is documented in `docs/operations/runbook.md` §Privacidad.

## 6. Operations

- `GET /metrics`: Prometheus text format, admin token required (or `--metrics-anonymous` on a trusted network).
  Per namespace: `latticelog_store_last_sequence`, `latticelog_ingest_records_stored_total`, `…_batches_stored_total`,
  `…_batches_duplicate_total`, `…_flushes_total`, `…_queue_depth`, `…_producers`, `…_failed`. Global:
  `latticelog_queries_running`, `latticelog_audit_events_dropped_total`, `latticelog_process_working_set_bytes`,
  `latticelog_gc_heap_bytes`; with replication, `latticelog_replication_lag_records` per namespace. The server does
  not log about itself into its own stores (no feedback loop, P24-T02).
- `GET /ui`: the web console (P23), static files embedded in the server with
  `Content-Security-Policy: default-src 'self'…; frame-ancestors 'none'`, `X-Frame-Options: DENY`. It calls the read
  API with the token the user types (kept in `sessionStorage` of that tab) and renders log content only as text.
- Every response carries `X-Content-Type-Options: nosniff` and `Referrer-Policy: no-referrer`.
- **Audit log** (`data/audit/audit-YYYYMMDD.jsonl`): one JSON object per line prefixed with its CRC-32. It records admin
  calls, reads (path and filters, never results), and authentication failures. Lines are written by a bounded
  background queue; if it overflows, events are counted in `latticelog_audit_events_dropped_total` rather than slowing
  requests. The file is append-only for the process but **not tamper-proof** against someone with access to the disk;
  ship it elsewhere if that matters. Note: the filters of reads and erasures are recorded and may contain personal data.
- **Brute-force brake**: 20 failed authentications from one address within a minute → 429 for that address until
  the window passes, whichever token it then presents.
- **Replication** (`--replicate-to <url>`, token in `LATTICELOG_REPLICA_TOKEN`): every namespace is forwarded to the
  same namespace on another server with the ingest protocol; see `docs/operations/replication.md`.

## 7. Not yet

- gRPC read API; query snapshots pinned across requests; per-tenant byte quotas for reads.
- Cluster: sharding, routing, consensus and automatic failover (P19); object storage tiers (P20).
- An `admin` token cannot run reads outside its namespaces unless they include `*`.
