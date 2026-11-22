// Mapping of logger objects (pino, winston) to records: the same rules the .NET importer applies to pino/structlog
// JSON (src/LatticeLog.OpenTelemetry/Otlp.cs, StructuredJsonlImporter), so live and migrated logs answer the same
// queries.

import type { Logger, RecordOptions } from "./logger.js";

/** Identifier-like event names: letters, digits, `_ . : - /`, no spaces, ≤ 128 characters, starting with a letter or `_`. */
export function looksLikeEventName(s: string): boolean {
  return s.length > 0 && s.length <= 128 && /^[\p{L}_][\p{L}\p{N}_.:\-/]*$/u.test(s);
}

const EVENT_KEYS = new Set(["event", "event_name", "event.name"]);
const TRACE_KEYS = new Set(["trace_id", "traceId"]);
const SPAN_KEYS = new Set(["span_id", "spanId"]);
const ERROR_KEYS = new Set(["err", "error"]);

/** pino numeric levels → OpenTelemetry severity: 10 trace, 20 debug, 30 info, 40 warn, 50 error, 60 fatal. */
export function pinoSeverity(level: number): number {
  if (level <= 10) return 1;
  if (level <= 20) return 5;
  if (level <= 30) return 9;
  if (level <= 40) return 13;
  if (level <= 50) return 17;
  return 21;
}

/**
 * Writes one record from a logger's fields. `fields` are the call's own key-values (typed values kept); the
 * recognized keys become record fields, every other key an attribute in its order:
 * - `event` / `event_name` / `event.name`: event name when identifier-like, else the message (or an attribute);
 * - `err` / `error` holding an Error: the exception (real type, stack, cause chain);
 * - `trace_id` / `traceId`, `span_id` / `spanId`: trace fields; `correlation_id`: correlation.
 */
export function writeFields(
  log: Logger,
  severity: number,
  message: string | undefined,
  fields: Iterable<[string, unknown]>,
  time?: Date | number,
  error?: unknown,
): boolean {
  if (!log.isEnabled(severity)) return false;
  const attrs: Array<[string, unknown]> = [];
  const o: RecordOptions = {};
  if (time !== undefined) o.time = time;
  if (error !== undefined) o.error = error;
  let msg = message;
  let eventText: string | undefined;
  let eventKey = "";
  for (const [k, v] of fields) {
    if (v === undefined) continue;
    if (EVENT_KEYS.has(k) && eventText === undefined && typeof v === "string") {
      eventText = v;
      eventKey = k;
    } else if (ERROR_KEYS.has(k) && o.error === undefined && v instanceof Error) {
      o.error = v;
    } else if (TRACE_KEYS.has(k) && o.traceId === undefined && typeof v === "string") {
      o.traceId = v;
    } else if (SPAN_KEYS.has(k) && o.spanId === undefined && typeof v === "string") {
      o.spanId = v;
    } else if (k === "correlation_id" && o.correlationId === undefined && typeof v === "string") {
      o.correlationId = v;
    } else {
      attrs.push([k, v]);
    }
  }
  if (eventText !== undefined) {
    if (looksLikeEventName(eventText)) o.event = eventText;
    else if (msg === undefined) msg = eventText;
    else attrs.push([eventKey, eventText]);
  }
  return log.log(severity, msg, attrs, o);
}
