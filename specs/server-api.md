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

## 5. Not yet (see docs/decisions/PHASE-17-REVIEW.md)

- **Not built yet:**
  - an online admin API for compaction and reindexing (compaction and verification exist offline in the CLI);
  - retention (age-based deletion), in any form;
  - a gRPC read API;
  - query snapshots pinned across requests;
  - per-tenant byte quotas for reads.
- **Not wired in:** an `admin` token cannot run reads outside its namespaces unless they include `*`.
