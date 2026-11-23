// pino destination: `pino(options, latticelog.pino.destination(log))`.
//
// pino serializes every call to a JSON line before a destination sees it, which loses types (`1.0` becomes `1`, a
// Date a string, a bigint cannot be serialized at all). This destination asks pino for the call's metadata
// (`Symbol.for("pino.metadata")`, the mechanism of `pino.multistream`): pino then hands over the original merging
// object with its real values, and the line is used only for what pino added (child bindings, `base`, mixins). It runs
// in the calling thread and does no I/O itself: records go to the logger's queue like any other call.

import type { Logger } from "./logger.js";
import { pinoSeverity, writeFields } from "./mapping.js";

export interface PinoDestinationOptions {
  /** pino's `messageKey` (default `msg`) and `errorKey` (default `err`), if you changed them. */
  messageKey?: string;
  errorKey?: string;
  /** Keys pino adds that should not become attributes (default `time` and `level`, which are record fields). */
  ignoreKeys?: string[];
}

/** The object pino writes to. `pino({ level: "info" }, destination(log))`. */
export interface PinoDestination {
  write(line: string): void;
  /** Set by pino before each `write` because of the metadata flag. */
  lastLevel: number;
  lastObj: unknown;
  lastMsg: unknown;
  lastTime: unknown;
  lastLogger: unknown;
  /** For `pino.final`/`logger.flush()`: flushes the LatticeLog logger (to the OS). */
  flush(cb?: (err?: Error) => void): void;
  flushSync(): void;
  end(): void;
}

/** A pino destination that writes into a LatticeLog logger, keeping the call's values typed. */
export function destination(log: Logger, options: PinoDestinationOptions = {}): PinoDestination {
  const messageKey = options.messageKey ?? "msg";
  const errorKey = options.errorKey ?? "err";
  const ignore = new Set(options.ignoreKeys ?? ["time", "level"]);
  const dest: PinoDestination & { [k: symbol]: boolean } = {
    [Symbol.for("pino.metadata")]: true,
    lastLevel: 0,
    lastObj: undefined,
    lastMsg: undefined,
    lastTime: undefined,
    lastLogger: undefined,
    write(line: string): void {
      let parsed: Record<string, unknown>;
      try {
        parsed = JSON.parse(line) as Record<string, unknown>;
      } catch {
        log.log(9, line.replace(/\n$/, ""), null, { event: "pino_unparsed_line" });
        return;
      }
      const level = typeof dest.lastLevel === "number" && dest.lastLevel > 0 ? dest.lastLevel : Number(parsed.level ?? 30);
      const obj = dest.lastObj;
      // The call's own object: its real values win over their JSON form in the line.
      let error: unknown;
      let own: Record<string, unknown> | undefined;
      if (obj instanceof Error) error = obj;
      else if (obj !== null && typeof obj === "object") own = obj as Record<string, unknown>;
      const fields: Array<[string, unknown]> = [];
      for (const k of Object.keys(parsed)) {
        if (ignore.has(k) || k === messageKey) continue;
        if (k === errorKey) {
          const real = own?.[k];
          if (real instanceof Error) error ??= real;
          else if (error === undefined) fields.push([k, parsed[k]]);
          continue;
        }
        if (obj instanceof Error && ["type", "message", "stack", "cause", "errors"].includes(k)) continue; // pino's err serializer
        fields.push([k, own && Object.prototype.hasOwnProperty.call(own, k) ? own[k] : parsed[k]]);
      }
      const msg = parsed[messageKey];
      // pino calls write() synchronously from the logging call, so the record time is taken here (nanosecond
      // digits) rather than from pino's millisecond `time`.
      writeFields(log, pinoSeverity(level), typeof msg === "string" ? msg : msg === undefined ? undefined : String(msg), fields, undefined, error);
    },
    flush(cb?: (err?: Error) => void): void {
      log.flush().then(
        (r) => cb?.(r.error ? new Error(r.error) : undefined),
        (e: Error) => cb?.(e),
      );
    },
    flushSync(): void {
      // Records are written at exit by the logger's own exit hook; nothing to do synchronously here.
    },
    end(): void {
      void log.close();
    },
  };
  return dest;
}
