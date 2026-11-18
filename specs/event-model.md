# LatticeLog event model v1 (P01)

Status: draft v1, implemented in `src/LatticeLog.Abstractions`. Independent of the binary encoding (`llog-format-v0.md`)
and of any programming language.

## 1. LogRecord

| field | type | required | semantics |
|---|---|---|---|
| `timestamp` | i64 Unix ns UTC | yes | When the event happened, producer wall clock. May be negative (before 1970). |
| `observed_timestamp` | i64 Unix ns | no | When a collector/SDK observed the record (import time for imported logs). |
| `monotonic` | i64 ns | no | Producer-local monotonic clock, only comparable within the same process run. |
| `severity` | 0..24 | yes | OpenTelemetry severity number; 0 = unspecified. Text: TRACE..FATAL with sub-levels (ERROR2…). |
| `event` | EventDescriptor ref | no | Typed contract the record was produced from. |
| `event_id` / `event_name` | u32 / string | no | Identity when there is no descriptor (bridged loggers, imports). |
| `body` | Value | no | Free text or raw bytes (absent ≠ null: `has_body`). |
| `attributes` | ordered list of (key, Value) | no | Own attributes; order preserved; duplicate keys allowed and preserved. |
| `trace_id` / `span_id` / `trace_flags` | 16 B / 8 B / u8 | no | W3C Trace Context. All-zero ids are invalid (= absent). |
| `operation_id` / `parent_operation_id` | string | no | Logical operation inside or across processes (explicit relation). |
| `correlation_id` | string | no | Business correlation (message id, order id…). |
| `exception` | ExceptionInfo | no | Type, message, stack text, inner exceptions (max depth 8, 16 inner per level). |
| `context` | ContextSnapshot ref | no | Innermost inherited context (see `context-model.md`). |
| `flags` | bit set | no | `redacted`, `raw_import`, `lossy_import`. |
| `sequence` | u64 | assigned | Store-wide, strictly increasing, assigned by the writer; basis of `event_ref`. |

### Identity (P01-T04)
- **Event type identity**: `(event_id, version)` of the descriptor. The same id MUST keep the same name across versions.
- **Schema version**: `EventDescriptor.version`; incompatible field changes require a new version (`schema-compat.md`).
- **Record instance identity**: `event_ref = llog://<store_id>/e/<sequence>`; stable across compaction because
  records keep their sequence. Across stores (server, replication) the store id disambiguates.
- **Operation identity**: `operation_id` (+ `parent_operation_id`), never inferred from timestamps.

## 2. Value types (P01-T03)

`null, bool, int64, uint64, float64, string, bytes, array, map, timestamp, duration, uuid, decimal`.
- float64 keeps its exact bit pattern (NaN payloads, −0.0).
- string is a sequence of UTF-16 code units in .NET; strings with unpaired surrogates are preserved exactly
  (special wire tag). Strings are **not normalized** (no NFC), byte/code-unit exactness is the contract.
- decimal: 96-bit mantissa + scale 0..28 + sign (scale preserved: `1.50` ≠ `1.5` as representation).
- map: ordered pairs, duplicates allowed.

## 3. Naming and limits (P01-T05)
- Event names: `^[a-z0-9][a-z0-9_.\-]{0,199}$` (enforced by the source generator).
- Reserved attribute prefixes: `latticelog.*` (internal), OpenTelemetry semantic conventions are recommended for the rest
  (`service.name`, `exception.type`, `code.function`, …).
- Limits: 4096 attributes per record, value nesting 32, context depth 64, exception depth 8, record block ≤ 64 MiB.

## 4. Source location (P01-T12)
Optional attributes `code.filepath` (repository-relative, never absolute private paths), `code.function`, `code.lineno`,
`vcs.repository.url`, `vcs.revision`. Not captured by default.

## 5. Sensitivity (P01-T14)
Descriptor fields carry `sensitivity`: `none`, `personal`, `secret`. Defaults: `secret` values are replaced by
`[REDACTED]` on the writer thread **before persistence** and the record is flagged `redacted`; attribute keys
matching sensitive patterns (password, token, authorization, cookie, api_key, …) are treated as secret. `personal`
is persisted and can be masked in outputs (`RedactPersonal`). Retention categories are a server concern (P18/P20).

## 6. Fingerprints (P01-T11)
`fp1 = sha256(event_name | exception type chain | normalized message shape)[0..16]` where the message shape is the
template if typed, otherwise the message with UUIDs, long hex, quoted strings and digits replaced. A fingerprint is a
grouping key; it is never presented as a root cause.

## 7. Exceptions (P01-T13)
`ExceptionInfo{type, message?, stack_trace?, inner[]}`. `AggregateException` keeps all inner exceptions (≤ 16).
Stack text is kept verbatim (it may contain paths; redact at the producer if needed).

## 8. Examples (P01-T18)
Example records for success, error, retry, timeout, fan-out and incomplete operations live in
`tests/GoldenFiles/v0.1/expected.llog.jsonl` and in the lab workloads (`research/LatticeLog.Lab/Workloads.cs`:
`queue` = retries, `microservices` = fan-out + deadline errors, `inventory` = timeouts, `batch` = long operations).
An *incomplete operation* is represented by the absence of its terminal event; readers MUST NOT synthesize it.

## 9. Mapping to OpenTelemetry (P01-T17)
See `otel-mapping.md`. Summary: timestamp→`TimeUnixNano`, observed→`ObservedTimestamp`, severity→`SeverityNumber`
(+`SeverityText`), event name→`EventName`, body→`Body`, attributes→`Attributes`, root context→`Resource`, scope
contexts→`InstrumentationScope`, trace/span/flags→`TraceId`/`SpanId`/`TraceFlags`.
