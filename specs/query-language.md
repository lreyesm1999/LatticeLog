# Query language v1 (P11-T02)

Filter expressions only: no functions, no code execution, bounded length (8 KiB), nesting (64) and IN lists (1024).

```
expr      := or
or        := and ("OR" and)*
and       := unary ("AND" unary)*
unary     := "NOT" unary | "(" expr ")" | "EXISTS" "(" field ")" | "TRUE" | predicate
predicate := field op literal | field "IN" "(" literal ("," literal)* ")" | field ("CONTAINS"|"~") literal
           | field "STARTSWITH" literal | field "IS" ["NOT"] "NULL"
op        := "=" | "==" | "!=" | "<>" | "<" | "<=" | ">" | ">="
literal   := 'single quoted' | "double quoted" | number | true | false | null | bare word (ERROR, hex ids, ...)
field     := builtin | ctx.<name> | attr.<name> | attributes.<name> | `quoted name` | <attribute name>
```

Builtins: `time` (RFC 3339 literal or Unix ns), `level|severity` (TRACE..FATAL, sub-levels, or 0..24), `event`,
`event_id`, `service`, `trace_id`, `span_id`, `operation_id`, `parent_operation_id`, `correlation_id`, `body|message`,
`seq`, `exception.type`, `exception.message`, `text` (body, event name, string attributes and exception text).

Semantics: typed comparison (numbers numerically across int/uint/double/decimal, strings ordinally, uuid and
timestamps parsed). A **missing field makes every comparison false, including `!=`**; use `EXISTS` / `IS NULL`.
`CONTAINS` and `STARTSWITH` are case-insensitive ordinal. Text search scans decoded candidates (no inverted index yet).

Execution: conjunctive constraints on time, level, event, service, trace/operation/correlation ids (bloom filter) and
`seq` prune segments and blocks using footer indexes before any block is decoded. Time and `seq` skip blocks in
every sealed segment; level, event and service skip blocks in segments written in format 0.3 or later (block
summaries, `llog-format-v0.md` §7.4) and only whole segments in older ones; trace/operation/correlation ids skip
whole segments only. `--explain` states this granularity. The full predicate is always re-evaluated on decoded
records, so indexes never change results (verified against a full-scan oracle in
`tests/LatticeLog.Tests/QueryTests.cs` and `BlockSummaryTests.cs`).

Consistency (P11-T17): a query sees sealed segments plus the complete blocks of the active segment at scan time
(`includes_unsealed_segments=true`). Pagination cursors encode (version, store, query hash, last sequence, order) and
are stable under compaction because sequences never change. Limits: `limit ≤ 10000`, scan limit, timeout and
cancellation; hitting one sets `partial_results` with a reason. Aggregations without filters other than whole-segment
time ranges are answered from footer counters without decoding blocks.
