# Evidence protocol `latticelog.evidence.v1` (P12)

Versioned independently of the binary format and of the query API (P12-T20). Implemented in `src/LatticeLog.Evidence`.

Every document has: `format`, `kind` (`overview | error_groups | event | related_events | trace | context_pack | schema`),
`store_id`, `query {version, filter}`, `trust {untrusted_fields[], note, redaction_applied}`, `coverage`, and
kind-specific sections (`summary`, `groups`, `relations`, `timeline`, `event` + `provenance`, `events`).

`coverage`: `matched_events`, `returned_events`, `omitted_events`, `truncated`, `partial_results`, `partial_reason`,
`sampling` (`none` or `priority-tiers-v1`), `budget_bytes`, `timeline_bytes`, `approx_tokens_chars_div_4`,
`exhaustive` ("yes" only if nothing was omitted and the scan was complete). A consumer MUST NOT present a document
with `truncated=true` or `partial_results=true` as exhaustive.

Timeline entry: `ref` (stable; resolvable with `latticelog event` or MCP `get_event`), `t`, `dt_us` (microseconds
relative to the first matched event), `sev`, `event`, `svc`, `relation` (related_events only), `trace`, `span`, `op`,
`msg` (rendered template or text body), `body`, `attrs`, `exception {type, message, top_frames[], inner[]}`, `flags`.

## Context Pack selection `priority-tiers-v1`

Records are added in this order until the timeline budget (75 % of `budget_bytes`) is used:
1. ERROR/FATAL records, at most 3 per fingerprint (first, last, middle);
2. first and last record of the scope;
3. WARN records, at most 2 per fingerprint;
4. ±2 neighbours of each error that share its trace or operation;
5. evenly spaced samples; 6. everything else in order.

Output is ordered by (time, sequence). Selection is deterministic: same store + same request ⇒ byte-identical document.

## Guarantees

- All numbers are computed from data; there is no generated prose.
- Groupings are labeled as fingerprints, never as causes; relations are explicit only (trace, operation,
  parent/child operation, correlation). Temporal proximity is never reported as a relation.
- Log-originated text is listed in `trust.untrusted_fields`; Markdown fences it with a fence longer than any backtick
  run it contains, so it cannot escape the code block.
- Redaction applies to every output (secret schema fields and sensitive keys), in addition to producer redaction.
- Token counts are approximations (chars/4); exact counts depend on the external model's tokenizer.
