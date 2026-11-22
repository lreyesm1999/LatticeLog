// winston transport: `winston.createLogger({ transports: [new LatticeLogTransport({ logger: log })] })`.
//
// winston hands transports the `info` object itself, so metadata keeps its real values (numbers, Dates, bigints,
// Buffers, nested objects). Needs `winston-transport` (installed with winston; an optional peer dependency).

import TransportStream from "winston-transport";
import type { Logger } from "./logger.js";
import { levelNumber } from "./logger.js";
import { writeFields } from "./mapping.js";

const SPLAT = Symbol.for("splat");
const LEVEL = Symbol.for("level");

export interface LatticeLogTransportOptions extends TransportStream.TransportStreamOptions {
  /** The LatticeLog logger to write into. */
  logger: Logger;
  /** Keys added by formats that are not attributes (default: `timestamp`, which becomes the record time). */
  ignoreKeys?: string[];
}

/** Severity of a winston level (npm and syslog level names; unknown names count as info). */
function severity(level: string): number {
  try {
    return levelNumber(level);
  } catch {
    return 9;
  }
}

export class LatticeLogTransport extends TransportStream {
  readonly #log: Logger;
  readonly #ignore: Set<string>;

  constructor(options: LatticeLogTransportOptions) {
    super(options);
    this.#log = options.logger;
    this.#ignore = new Set(options.ignoreKeys ?? ["timestamp"]);
  }

  override log(info: Record<string | symbol, unknown>, callback: () => void): void {
    // The original level name (formats such as colorize change `info.level`).
    const level = typeof info[LEVEL] === "string" ? (info[LEVEL] as string) : String(info.level);
    const fields: Array<[string, unknown]> = [];
    let error: unknown = info instanceof Error ? info : undefined;
    for (const k of Object.keys(info)) {
      if (k === "level" || k === "message" || this.#ignore.has(k)) continue;
      if (error === info && (k === "stack" || k === "name")) continue;
      fields.push([k, info[k]]);
    }
    // `logger.error("failed", err)`: winston merges the error's message into `message`; the Error itself stays in splat.
    if (error === undefined && Array.isArray(info[SPLAT])) error = (info[SPLAT] as unknown[]).find((x) => x instanceof Error);
    let time: Date | undefined;
    if (typeof info.timestamp === "string" && this.#ignore.has("timestamp")) {
      const d = new Date(info.timestamp);
      if (Number.isFinite(d.getTime())) time = d;
    }
    const message = info.message;
    writeFields(this.#log, severity(level), message === undefined || message === "" ? undefined : String(message), fields, time, error);
    callback();
  }

  override close(): void {
    void this.#log.flush();
  }
}

export default LatticeLogTransport;
