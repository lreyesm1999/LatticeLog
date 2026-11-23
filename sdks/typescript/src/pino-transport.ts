// pino transport (runs in pino's worker thread): `pino.transport({ target: "latticelog/pino-transport", options })`.
//
// The worker receives pino's JSON lines, so values have their JSON types only (`1.0` arrives as `1`, a Date as a
// string). For exact types use the in-process destination (`latticelog/pino`). The worker opens the folder itself,
// so the logging thread does no LatticeLog work at all.

import { Writable } from "node:stream";
import { type OpenOptions, open } from "./logger.js";
import { pinoSeverity, writeFields } from "./mapping.js";

export interface PinoTransportOptions extends OpenOptions {
  /** The service folder (required). */
  folder: string;
  messageKey?: string;
  errorKey?: string;
}

/** Turns pino's serialized error (`{type, message, stack, cause}`) back into an Error with its type. */
function reviveError(v: unknown): unknown {
  if (v === null || typeof v !== "object") return v;
  const o = v as { type?: unknown; message?: unknown; stack?: unknown; cause?: unknown; aggregateErrors?: unknown };
  if (typeof o.message !== "string" && typeof o.stack !== "string") return v;
  const e = new Error(typeof o.message === "string" ? o.message : "");
  e.name = typeof o.type === "string" ? o.type : "Error";
  if (typeof o.stack === "string") e.stack = o.stack;
  if (o.cause !== undefined) (e as { cause?: unknown }).cause = reviveError(o.cause);
  if (Array.isArray(o.aggregateErrors)) (e as unknown as { errors: unknown[] }).errors = o.aggregateErrors.map(reviveError);
  return e;
}

export default async function latticelogTransport(options: PinoTransportOptions): Promise<Writable> {
  const { folder, messageKey = "msg", errorKey = "err", ...openOptions } = options;
  const log = open(folder, { ...openOptions, exitHook: openOptions.exitHook ?? true });
  let partial = "";
  const handle = (line: string): void => {
    if (!line) return;
    let parsed: Record<string, unknown>;
    try {
      parsed = JSON.parse(line) as Record<string, unknown>;
    } catch {
      log.log(9, line, null, { event: "pino_unparsed_line" });
      return;
    }
    const fields: Array<[string, unknown]> = [];
    let error: unknown;
    for (const [k, v] of Object.entries(parsed)) {
      if (k === "level" || k === "time" || k === messageKey) continue;
      if (k === errorKey) {
        const e = reviveError(v);
        if (e instanceof Error) error = e;
        else fields.push([k, v]);
        continue;
      }
      fields.push([k, v]);
    }
    const msg = parsed[messageKey];
    const time = typeof parsed.time === "number" ? parsed.time : undefined;
    writeFields(log, pinoSeverity(Number(parsed.level ?? 30)), msg === undefined ? undefined : String(msg), fields, time, error);
  };
  return new Writable({
    decodeStrings: false,
    write(chunk: string | Buffer, _enc, cb): void {
      const text = partial + (typeof chunk === "string" ? chunk : chunk.toString("utf8"));
      let start = 0;
      for (let nl = text.indexOf("\n"); nl >= 0; nl = text.indexOf("\n", start)) {
        handle(text.slice(start, nl));
        start = nl + 1;
      }
      partial = text.slice(start);
      cb();
    },
    final(cb): void {
      if (partial) handle(partial);
      partial = "";
      log.close().then(
        () => cb(),
        (e: Error) => cb(e),
      );
    },
  });
}
