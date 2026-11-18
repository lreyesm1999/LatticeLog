# OpenTelemetry mapping (P13-T01..T09)

Export (`latticelog export --format otlp-json`) writes an OTLP/JSON `ExportLogsServiceRequest` (proto3 JSON mapping,
hex trace/span ids, int64 as strings). Import accepts the same shape with limits (records, attributes, depth, bytes).

| LatticeLog | OpenTelemetry | fidelity |
|---|---|---|
| timestamp / observed | timeUnixNano / observedTimeUnixNano | exact (negative timestamps counted as loss) |
| severity | severityNumber + severityText | exact |
| event name | eventName | exact; descriptor fields/template are not exported (counted) |
| event id / version | attributes `latticelog.event.id`, `latticelog.event.version` | id round-trips |
| body, ordered attributes | body, attributes | exact for string, bool, int64, double, bytes, array, map |
| uint64, timestamp, duration, uuid, decimal values | intValue / stringValue | type lost (counted) |
| NaN payloads, unpaired surrogates | — | counted |
| root context | resource | exact |
| inner contexts | flattened into record attributes | own-vs-inherited provenance lost (counted) |
| trace_id / span_id / trace_flags | traceId / spanId / flags | exact |
| operation / parent / correlation ids | `latticelog.*` attributes | round-trip |
| exception | `exception.type`, `exception.message`, `exception.stacktrace` | inner exceptions dropped (counted) |
| monotonic time, record flags | — | dropped (counted) |

Every loss is reported in a `LossReport` that the CLI prints; nothing is lost silently.

Other importers:
- Structured JSONL recognizes Serilog CLEF (`@t @l @m @mt @x @tr @sp`), pino (numeric levels, epoch ms) and generic
  keys. Lines that are not JSON objects are kept as text records flagged `lossy_import`. Numeric timestamps are
  interpreted by magnitude (ns > 1e17, µs > 1e14, ms > 1e11, else seconds) — a documented heuristic.
- Raw text stores each line's exact bytes (`raw_import`); the exporter reproduces the file byte for byte.

Not implemented yet: OTLP/gRPC and OTLP/HTTP transport, OpenTelemetry Collector integration, Serilog/NLog sinks
(P13-T03 partial, P13-T08 partial, P13-T11 pending).
