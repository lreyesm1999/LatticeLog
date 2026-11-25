# Evidence protocol `latticelog.evidence.v1` (P12)

Versioned independently of the binary format and of the query API (P12-T20). Implemented in `src/LatticeLog.Evidence`.

Every document has: `format`, `kind` (`overview | error_groups | event | related_events | trace | context_pack | schema`),
`store_id`, `query {version, filter}`, `trust {untrusted_fields[], note, redaction_applied}`, `coverage`, and
kind-specific sections (`summary`, `groups`, `relations`, `timeline`, `event` + `provenance`, `events`).

`coverage`: `matched_events`, `returned_events`, `omitted_events`, `truncated`, `partial_results`, `partial_reason`,
`sampling` (`none` or `priority-tiers-v1`), `budget_bytes`, `timeline_bytes`, `approx_tokens_chars_div_4`,
`exhaustive` ("yes" only if nothing was omitted and the scan was complete). A consumer MUST NOT present a document
with `truncated=true` or `partial_results=true` as exhaustive.

Context packs retain `format: latticelog.evidence.v1` for compatibility and add `context_version: "2"`. V2 applies
`budget_bytes` to the complete structured evidence payload, not only the timeline, and adds
`requested_budget_bytes`, `effective_budget_bytes`, `budget_scope`, `candidate_events`,
`candidate_window_truncated`, and an optional `continuation_token`. The old `budget_bytes` and
`approx_tokens_chars_div_4` fields remain as compatibility aliases.

Timeline entry: `ref` (stable; resolvable with `latticelog event` or MCP `get_event`), `t`, `dt_us` (microseconds
relative to the first matched event), `sev`, `event`, `svc`, `relation` (related_events only), `trace`, `span`, `op`,
`msg` (rendered template or text body), `body`, `attrs`, `exception {type, message, top_frames[], inner[]}`, `flags`.

## Context Pack V2 selection `priority-tiers-v1`

Records are considered in this order, then the complete payload is measured and lower-priority entries are removed
until the effective byte budget is met:
1. ERROR/FATAL records, at most 3 per fingerprint (first, last, middle);
2. first and last record of the scope;
3. WARN records, at most 2 per fingerprint;
4. ±2 neighbours of each error that share its trace or operation;
5. evenly spaced samples; 6. everything else in order.

Output is ordered by (time, sequence). Selection is deterministic: same store + same request ⇒ byte-identical document.

MCP callers can select `detail_level=summary|investigation|evidence`, `max_events`, `max_groups`,
`include_evidence`, `budget_tokens`, and `continuation_token`. `summary` omits the compact timeline;
`investigation` includes selected entries and explicit relations; `evidence` also describes direct recovery through
`get_event`. Every selected item is represented by a stable `evidence_ref`.

`budget_tokens` is an estimated ceiling using `ceil(UTF-8 bytes / 4)`. It is deliberately labeled estimated; an
external consumer with a model-specific tokenizer should count the returned payload itself. When both byte and token
budgets are supplied, the smaller derived byte ceiling wins.

Relations carry `classification`, `basis`, and `evidence_refs`. The current graph emits `observed` operation-parent
edges only when explicit identifiers exist. Missing parent-span data stays unknown; wall-clock order is never promoted
to causality.

## Guarantees

- All numbers are computed from data; there is no generated prose.
- Groupings are labeled as fingerprints, never as causes; relations are explicit only (trace, operation,
  parent/child operation, correlation). Temporal proximity is never reported as a relation.
- Log-originated text is listed in `trust.untrusted_fields`; Markdown fences it with a fence longer than any backtick
  run it contains, so it cannot escape the code block.
- Redaction applies to every output (secret schema fields and sensitive keys), in addition to producer redaction.
- Token counts are approximations; exact counts depend on the external model's tokenizer.
