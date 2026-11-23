// Logger: the logging call (encoding into a string) and the writer (dense sequences, contexts, files).
//
// The logging call never touches a file: it encodes the record into one string and appends it to an in-memory queue.
// The writer runs on the event loop: on a timer (10 ms Balanced, 100 ms Fast), when the queue passes a size
// threshold, or at once in Durable mode, it swaps the queue out, assigns dense sequences, resolves contexts to
// file-local refs and appends the lines with positional writes that run on libuv's thread pool. fsync never runs on
// the logging call either: rotated files and the periodic Balanced fsync are handed to the thread pool too, and
// `flush({ fsync: true })`, `close()` and Durable mode wait for them before they report `FsyncConfirmed`.

import * as fs from "node:fs";
import * as path from "node:path";
import { Context, type ContextKind, contextStorage, randomOperationId } from "./context.js";
import { encodeException } from "./exception.js";
import {
  PRODUCER,
  type ProcessFolder,
  acquireProcessFolder,
  applyRetention,
  ensureManifest,
  fileName,
  releaseLock,
} from "./folder.js";
import { Shipper, type ServerOptions, type ShipStats } from "./ship.js";
import { type Attributes, dateTimestamp, encodePairs, encodeString, formatTimestamp, secondPrefix } from "./values.js";

/** Severity numbers (OpenTelemetry). */
export const Level = { trace: 1, debug: 5, info: 9, warn: 13, error: 17, fatal: 21 } as const;
export type LevelName = keyof typeof Level;

/** When accepted records reach the disk (specs/durability.md). Writing is asynchronous in every mode. */
export type Durability = "fast" | "balanced" | "durable";

/** What the logging call does when the queue is full. */
export type Saturation =
  /** Drop the new record (counted in `droppedSaturation`). Default: the logging call never does file I/O. */
  | "drop"
  /** Write the queue out synchronously from the logging call, then accept the record: lossless, but the call blocks. */
  | "block";

export interface OpenOptions {
  /** The name LatticeLog finds the source by (`service.name`). Required. */
  service: string;
  /** `deployment.environment.name` of the resource, also written to the manifest. */
  environment?: string;
  /** Default `balanced`. */
  durability?: Durability;
  /** Records below this level are skipped (not counted). Default `info`. */
  minLevel?: LevelName | number;
  /** More resource attributes, inherited by every record (e.g. `{"service.version": "1.4.2"}`). */
  resource?: Attributes;
  /** Retention by age: whole closed files older than this many milliseconds are deleted, oldest first. */
  maxAgeMs?: number;
  /** Retention by size of the whole source folder, in bytes. */
  maxBytes?: number;
  /** A new file is started when the current one reaches this size (default 16 MiB). */
  fileMaxBytes?: number;
  /** Characters of encoded records waiting for the writer before `saturation` applies (default 16 Mi). */
  queueChars?: number;
  /** Default `drop`. */
  saturation?: Saturation;
  /** Write what is still queued (synchronously) when the process exits. Default true. */
  exitHook?: boolean;
  /** Ships the folder to latticelog-server with ingest protocol v1. */
  server?: ServerOptions;
  /** How long `close()` waits for the server to confirm what is left (server mode, default 5000 ms). */
  closeTimeoutMs?: number;
}

/** Fields of one record besides its message and attributes. */
export interface RecordOptions {
  /** Event name (e.g. `order_placed`), the identity LatticeLog groups and queries by. */
  event?: string;
  /** An error with its real type, stack and `cause` chain (`AggregateError.errors` too). */
  error?: unknown;
  /** W3C trace id (32 hex) and span id (16 hex); invalid or all-zero ids are ignored. */
  traceId?: string;
  spanId?: string;
  /** Business correlation id (message id, order id…). */
  correlationId?: string;
  /** Record time (default: now). A `Date`, Unix milliseconds, or Unix nanoseconds as a bigint. */
  time?: Date | number | bigint;
  /** The context (default: the current async context, else the logger's resource context). */
  context?: Context;
}

export type FlushLevel = "None" | "WrittenToOs" | "FsyncConfirmed";

/** Outcome of `flush()`: covers every record accepted before the call. */
export interface FlushResult {
  level: FlushLevel;
  /** Highest sequence written so far. */
  lastSeq: number;
  /** The last write or fsync error since the previous flush, if any (its records are counted as dropped). */
  error?: string;
}

/** Counters. Every loss is counted; nothing is dropped silently. */
export interface Stats extends ShipStats {
  /** Records written to a file (handed to the OS). */
  written: number;
  /** Highest sequence written. */
  lastSeq: number;
  /** Records waiting in memory. */
  queued: number;
  droppedSaturation: number;
  droppedWriteError: number;
  /** Server mode: refused because the unconfirmed backlog reached `maxSpoolBytes`. */
  droppedSpoolFull: number;
  droppedAfterClose: number;
}

const FILE_MAX_BYTES = 16 * 1024 * 1024;
const QUEUE_CHARS = 16 * 1024 * 1024;
/** The writer is woken at once (not at the next tick) when this many characters are queued. */
const WAKE_CHARS = 512 * 1024;
const CHUNK = 1024 * 1024;
const BALANCED_FSYNC_MS = 1000;
const TRACE_RE = /^[0-9a-fA-F]{32}$/;
const SPAN_RE = /^[0-9a-fA-F]{16}$/;

// ------------------------------------------------------------------------------------------------- time

// Wall clock in nanoseconds without a bigint per call: `Date.now()` (milliseconds) re-anchored every second, the
// sub-millisecond part from the monotonic clock.
let anchorMs = Date.now();
let anchorPerf = performance.now();
let lastSec = -1;
let lastPrefix = "";
let lastNs = 0; // nanoseconds within the second of the previous call (keeps time from going backwards in a second)
let lastWhole = 0;

/** The current time as a JSON string with 9 fractional digits. */
function nowTimestamp(): string {
  let elapsed = performance.now() - anchorPerf;
  if (elapsed >= 1000 || elapsed < 0) {
    anchorMs = Date.now();
    anchorPerf = performance.now();
    elapsed = 0;
  }
  const ms = anchorMs + elapsed;
  const whole = Math.floor(ms);
  const sec = Math.floor(whole / 1000);
  let ns = (whole - sec * 1000) * 1_000_000 + Math.floor((ms - whole) * 1_000_000);
  if (sec !== lastSec) {
    lastSec = sec;
    lastPrefix = '"' + secondPrefix(sec) + ".";
  } else if (ns < lastNs && whole <= lastWhole) {
    ns = lastNs;
  }
  lastNs = ns;
  lastWhole = whole;
  return lastPrefix + String(ns).padStart(9, "0") + 'Z"';
}

function timeJson(t: Date | number | bigint): string {
  if (typeof t === "bigint") return '"' + formatTimestamp(t) + '"';
  if (typeof t === "number") return dateTimestamp(new Date(t)) ?? nowTimestamp();
  return dateTimestamp(t) ?? nowTimestamp();
}

// ------------------------------------------------------------------------------------------------- files

/** An open data file. Writes are positional; `close` waits for operations still running on the thread pool. */
class DataFile {
  readonly fd: number;
  readonly path: string;
  size: number;
  private inflight = 0;
  private idle: Array<() => void> = [];

  private constructor(file: string, fd: number, size: number) {
    this.path = file;
    this.fd = fd;
    this.size = size;
  }

  // Read/write, created when missing, never truncated, not O_APPEND (positional writes must land where asked).
  static readonly FLAGS = fs.constants.O_RDWR | fs.constants.O_CREAT;

  static openSync(file: string): DataFile {
    const fd = fs.openSync(file, DataFile.FLAGS);
    return new DataFile(file, fd, fs.fstatSync(fd).size);
  }

  /** Opens on the thread pool: on NTFS, creating a file can wait for an fsync of another file of the same volume. */
  static open(file: string): Promise<DataFile> {
    return new Promise((resolve, reject) =>
      fs.open(file, DataFile.FLAGS, (e, fd) => {
        if (e) return reject(e);
        fs.fstat(fd, (e2, st) => {
          if (e2) {
            fs.close(fd, () => reject(e2));
            return;
          }
          resolve(new DataFile(file, fd, st.size));
        });
      }),
    );
  }

  private track<T>(p: Promise<T>): Promise<T> {
    this.inflight++;
    return p.finally(() => {
      if (--this.inflight === 0) for (const f of this.idle.splice(0)) f();
    });
  }

  write(buf: Buffer, len: number, pos: number): Promise<void> {
    return this.track(
      new Promise<void>((resolve, reject) => {
        const step = (off: number): void => {
          fs.write(this.fd, buf, off, len - off, pos + off, (err, n) => {
            if (err) return reject(err);
            if (off + n < len) return step(off + n);
            resolve();
          });
        };
        step(0);
      }),
    );
  }

  datasync(): Promise<void> {
    return this.track(new Promise<void>((resolve, reject) => fs.fdatasync(this.fd, (e) => (e ? reject(e) : resolve()))));
  }

  private closing: Promise<void> | undefined;

  /** Closes once every running operation finished. Idempotent. */
  close(): Promise<void> {
    this.closing ??= (async () => {
      if (this.inflight) await new Promise<void>((r) => this.idle.push(r));
      await new Promise<void>((resolve) => fs.close(this.fd, () => resolve()));
    })();
    return this.closing;
  }

  get closed(): boolean {
    return this.closing !== undefined;
  }
}

/** Encoded lines of one file, ready to be written at `pos`. */
interface Segment {
  file: DataFile;
  buf: Buffer;
  len: number;
  pos: number;
  firstSeq: number;
  records: number;
}

interface FlushWaiter {
  fsync: boolean;
  resolve: (r: FlushResult) => void;
}

const exitHooks = new Set<Logger>();
let exitHookInstalled = false;
function installExitHook(): void {
  if (exitHookInstalled) return;
  exitHookInstalled = true;
  process.on("exit", () => {
    for (const l of exitHooks) l._exitSync();
  });
}

// ------------------------------------------------------------------------------------------------- Logger

/**
 * A LatticeLog logger for one service folder. Create it with {@link open}. Logging calls return `true` when the
 * record was accepted in memory (never "persisted"): `flush()` reports the level actually reached.
 */
export class Logger {
  readonly folder: string;
  readonly processDir: string;
  /** 32 hex digits: the `store_id` of this process folder (and the producer id when shipping). */
  readonly producerId: string;
  /** The resource context (service, environment, resource attributes). */
  readonly root: Context;

  #minLevel: number;
  readonly #durability: Durability;
  readonly #saturation: Saturation;
  readonly #capacity: number;
  readonly #fileMaxBytes: number;
  readonly #maxAgeMs: number | undefined;
  readonly #maxBytes: number | undefined;
  readonly #lock: string;
  readonly #closeTimeoutMs: number;
  readonly #header: string;

  // queue (filled by logging calls)
  #queue: string[] = [];
  #ctxs: Array<Context | undefined> = [];
  #queuedChars = 0;
  #closed = false;

  // writer state
  #nextSeq: number;
  #file: DataFile | undefined;
  #refs = new Map<number, number>();
  #nextRef = 1;
  #needHeader = false;
  #openFailed = false;
  #segments: Segment[] = [];
  #segIndex = 0;
  #writing = false;
  #timer: NodeJS.Timeout | undefined;
  #fsyncTimer: NodeJS.Timeout | undefined;
  #immediate: NodeJS.Immediate | undefined;
  #flushWaiters: FlushWaiter[] = [];
  #dirty = false;
  #lastFsync = Date.now();
  #periodicSync: Promise<void> | undefined;
  /**
   * The durability chain: fsync + close of retired files, one at a time (like the Rust SDK's durability thread). A
   * 16 MiB fsync takes ~250 ms on an HDD; run in parallel they would occupy every thread of libuv's pool (4 by
   * default) and stall the writes of the current file.
   */
  #syncTail: Promise<void> = Promise.resolve();
  #syncPending = 0;
  /** Closes of abandoned files (after a write error), without fsync. */
  #syncs: Array<Promise<void>> = [];
  #background: Promise<void> = Promise.resolve();
  #error: string | undefined;
  #fsyncError: string | undefined;
  #spoolFull = false;
  #closing: Promise<void> | undefined;
  #exited = false;

  #written = 0;
  #lastSeq: number;
  #droppedSaturation = 0;
  #droppedWriteError = 0;
  #droppedSpoolFull = 0;
  #droppedAfterClose = 0;

  readonly #shipper: Shipper | undefined;

  /** @internal Use {@link open}. */
  constructor(folder: string, options: OpenOptions, pf: ProcessFolder) {
    this.folder = folder;
    this.processDir = pf.dir;
    this.producerId = pf.producerId.toString("hex");
    this.#lock = pf.lock;
    this.#nextSeq = pf.nextSeq;
    this.#lastSeq = pf.nextSeq - 1;
    this.#minLevel = levelNumber(options.minLevel ?? "info");
    this.#durability = options.durability ?? "balanced";
    this.#saturation = options.saturation ?? "drop";
    this.#capacity = Math.max(64 * 1024, options.queueChars ?? QUEUE_CHARS);
    this.#fileMaxBytes = Math.max(4096, options.fileMaxBytes ?? FILE_MAX_BYTES);
    this.#maxAgeMs = options.maxAgeMs;
    this.#maxBytes = options.maxBytes;
    this.#closeTimeoutMs = options.closeTimeoutMs ?? 5000;
    this.#header = '{"type":"header","format":"latticelog.jsonl","version":1,"store_id":"' + this.producerId + '"}\n';

    const resource: Array<[string, unknown]> = [["service.name", options.service]];
    if (options.environment !== undefined) resource.push(["deployment.environment.name", options.environment]);
    if (options.resource) {
      if (Array.isArray(options.resource)) resource.push(...(options.resource as Array<[string, unknown]>));
      else if (options.resource instanceof Map) resource.push(...(options.resource as Map<string, unknown>));
      else resource.push(...Object.entries(options.resource));
    }
    this.root = new Context("Resource", undefined, undefined, resource);

    if (options.server) {
      this.#shipper = new Shipper(options.server, pf, (full) => {
        this.#spoolFull = full;
      });
    }
    if (options.exitHook ?? true) {
      exitHooks.add(this);
      installExitHook();
    }
    this.#retention();
    this.#shipper?.start();
  }

  // --------------------------------------------------------------------------------------------- logging calls

  /** True when records of `level` are written. */
  isEnabled(level: LevelName | number): boolean {
    return levelNumber(level) >= this.#minLevel;
  }

  setMinLevel(level: LevelName | number): void {
    this.#minLevel = levelNumber(level);
  }

  trace(message: unknown, attributes?: Attributes | null, options?: RecordOptions): boolean {
    return Level.trace < this.#minLevel ? false : this.#emit(Level.trace, message, attributes, options);
  }
  debug(message: unknown, attributes?: Attributes | null, options?: RecordOptions): boolean {
    return Level.debug < this.#minLevel ? false : this.#emit(Level.debug, message, attributes, options);
  }
  info(message: unknown, attributes?: Attributes | null, options?: RecordOptions): boolean {
    return Level.info < this.#minLevel ? false : this.#emit(Level.info, message, attributes, options);
  }
  warn(message: unknown, attributes?: Attributes | null, options?: RecordOptions): boolean {
    return Level.warn < this.#minLevel ? false : this.#emit(Level.warn, message, attributes, options);
  }
  error(message: unknown, attributes?: Attributes | null, options?: RecordOptions): boolean {
    return Level.error < this.#minLevel ? false : this.#emit(Level.error, message, attributes, options);
  }
  fatal(message: unknown, attributes?: Attributes | null, options?: RecordOptions): boolean {
    return Level.fatal < this.#minLevel ? false : this.#emit(Level.fatal, message, attributes, options);
  }

  /** A record at any level (a name or an OpenTelemetry severity number 1–24). `message` null or undefined = no body. */
  log(level: LevelName | number, message: unknown, attributes?: Attributes | null, options?: RecordOptions): boolean {
    const n = levelNumber(level);
    return n < this.#minLevel ? false : this.#emit(n, message, attributes, options);
  }

  /** A named event without a message: `log.event("order_placed", { order_id: 991 })` (level info by default). */
  event(name: string, attributes?: Attributes | null, options?: RecordOptions & { level?: LevelName | number }): boolean {
    const n = levelNumber(options?.level ?? "info");
    return n < this.#minLevel ? false : this.#emit(n, undefined, attributes, { ...options, event: name });
  }

  #emit(level: number, message: unknown, attributes: Attributes | null | undefined, o: RecordOptions | undefined): boolean {
    if (this.#closed) {
      this.#droppedAfterClose++;
      return false;
    }
    if (message instanceof Error && o?.error === undefined) {
      // log.error(err): the message is the error's, the error itself the exception.
      o = { ...o, error: message };
      message = message.message;
    }
    let s = '"time":' + (o?.time === undefined ? nowTimestamp() : timeJson(o.time)) + ',"severity_number":' + level;
    if (message !== undefined && message !== null) s += ',"body":' + encodeString(typeof message === "string" ? message : String(message));
    if (attributes) {
      const pairs = encodePairs(attributes);
      if (pairs) s += ',"attributes":[' + pairs + "]";
    }
    let ctx: Context | undefined;
    if (o) {
      if (o.event !== undefined) s += ',"event":' + encodeString(o.event);
      if (o.traceId !== undefined && TRACE_RE.test(o.traceId) && !/^0+$/.test(o.traceId)) {
        s += ',"trace_id":"' + o.traceId.toLowerCase() + '"';
        if (o.spanId !== undefined && SPAN_RE.test(o.spanId) && !/^0+$/.test(o.spanId)) s += ',"span_id":"' + o.spanId.toLowerCase() + '"';
      }
      if (o.correlationId !== undefined) s += ',"correlation_id":' + encodeString(o.correlationId);
      if (o.error !== undefined) s += ',"exception":' + encodeException(o.error);
      ctx = o.context;
    }
    if (ctx === undefined) ctx = contextStorage.getStore();
    if (ctx !== undefined) s += ctx.opFields;

    if (this.#queuedChars + s.length > this.#capacity && this.#queue.length) {
      if (this.#saturation === "drop") {
        this.#droppedSaturation++;
        return false;
      }
      this.#drainSync(); // "block": the calling thread writes the backlog itself
    }
    this.#queue.push(s);
    this.#ctxs.push(ctx);
    this.#queuedChars += s.length;
    this.#schedule();
    return true;
  }

  // --------------------------------------------------------------------------------------------- contexts

  /** The current async context, else the resource context: the parent of new contexts. */
  #parent(): Context {
    return contextStorage.getStore() ?? this.root;
  }

  /** A named scope under the current context: `log.scope("import", { file }).run(() => …)`. */
  scope(name: string, attributes?: Attributes): Context {
    return new Context("Scope", name, this.#parent(), attributes);
  }

  /**
   * An operation: records inside carry `operation_id` (random when `id` is omitted) and the enclosing operation as
   * `parent_operation_id`. `await log.operation("checkout", { id: "op-991" }).run(async () => …)`.
   */
  operation(name: string, options?: { id?: string; attributes?: Attributes }): Context {
    return new Context("Operation", name, this.#parent(), options?.attributes, options?.id ?? randomOperationId());
  }

  /** A context of any kind under the current context. */
  context(kind: ContextKind, name?: string, attributes?: Attributes): Context {
    return new Context(kind, name, this.#parent(), attributes);
  }

  // --------------------------------------------------------------------------------------------- writer

  #schedule(): void {
    if (this.#writing) return; // the running writer loop takes it
    if (this.#durability === "durable" || this.#queuedChars >= WAKE_CHARS) {
      if (!this.#immediate) this.#immediate = setImmediate(() => this.#run());
      return;
    }
    if (!this.#timer) {
      this.#timer = setTimeout(() => this.#run(), this.#durability === "fast" ? 100 : 10);
      this.#timer.unref();
    }
  }

  #cancelWake(): void {
    if (this.#timer) {
      clearTimeout(this.#timer);
      this.#timer = undefined;
    }
    if (this.#immediate) {
      clearImmediate(this.#immediate);
      this.#immediate = undefined;
    }
  }

  #cancelFsyncTimer(): void {
    if (this.#fsyncTimer) {
      clearTimeout(this.#fsyncTimer);
      this.#fsyncTimer = undefined;
    }
  }

  /** The writer loop: one batch at a time until the queue is empty and every flush is answered. */
  async #run(): Promise<void> {
    this.#cancelWake();
    if (this.#writing || this.#exited) return;
    this.#writing = true;
    try {
      while (this.#queue.length || this.#flushWaiters.length) {
        const waiters = this.#flushWaiters;
        this.#flushWaiters = [];
        const needFile = this.#encodeQueue(false);
        await this.#writeSegments();
        if (needFile) {
          // Rotation: the rest of the batch waits in the queue for the new file, and so do the flushes.
          await this.#openNext();
          this.#flushWaiters.unshift(...waiters);
          continue;
        }
        const fsync = this.#durability === "durable" || waiters.some((w) => w.fsync);
        let synced = false;
        if (fsync) synced = await this.#syncAll();
        else this.#periodicFsync();
        if (waiters.length) {
          const level: FlushLevel = this.#error !== undefined && this.#file === undefined ? "None" : synced ? "FsyncConfirmed" : "WrittenToOs";
          const r: FlushResult = { level, lastSeq: this.#lastSeq };
          if (this.#error !== undefined) r.error = this.#error;
          this.#error = undefined;
          for (const w of waiters) w.resolve(r);
        }
        this.#shipper?.notifyWritten();
      }
    } finally {
      this.#writing = false;
    }
    if (this.#queue.length) this.#schedule();
    else if (this.#durability === "balanced" && this.#dirty && !this.#fsyncTimer) {
      // Make sure the periodic fsync happens even when nothing more is logged.
      this.#fsyncTimer = setTimeout(() => this.#periodicFsyncTick(), BALANCED_FSYNC_MS);
      this.#fsyncTimer.unref();
    }
  }

  #periodicFsyncTick(): void {
    this.#fsyncTimer = undefined;
    if (this.#writing || this.#closed) return;
    this.#periodicFsync();
    if (this.#dirty) {
      this.#fsyncTimer = setTimeout(() => this.#periodicFsyncTick(), BALANCED_FSYNC_MS);
      this.#fsyncTimer.unref();
    }
  }

  /** Balanced: once a second, an fsync of the current file on the thread pool (skipped while one is running). */
  #periodicFsync(): void {
    if (this.#durability !== "balanced" || !this.#dirty || !this.#file || this.#periodicSync || this.#syncPending) return;
    if (Date.now() - this.#lastFsync < BALANCED_FSYNC_MS) return;
    const f = this.#file;
    this.#dirty = false;
    this.#lastFsync = Date.now();
    const p = f.datasync().then(
      () => undefined,
      (e: Error) => {
        this.#fsyncError = `fsync: ${e.message}`;
      },
    );
    this.#periodicSync = p.finally(() => {
      this.#periodicSync = undefined;
    });
  }

  /** fsync of the current file, after every fsync handed to the thread pool before it (rotated files, periodic). */
  async #syncAll(): Promise<boolean> {
    let ok = true;
    if (this.#file && this.#dirty) {
      try {
        await this.#file.datasync();
        this.#dirty = false;
        this.#lastFsync = Date.now();
      } catch (e) {
        this.#error = `fsync: ${(e as Error).message}`;
        ok = false;
      }
    }
    while (this.#syncs.length || this.#periodicSync || this.#syncPending) {
      await Promise.all([...this.#syncs.splice(0), this.#periodicSync, this.#syncTail]);
    }
    if (this.#fsyncError !== undefined) {
      this.#error = this.#fsyncError;
      this.#fsyncError = undefined;
      ok = false;
    }
    return ok && this.#error === undefined;
  }

  /**
   * Turns the queue into segments (encoded lines per file). When a new file is needed, `syncOpen` opens it inline
   * (exit hook, saturation "block"); otherwise encoding stops there, the rest stays queued, and the caller opens the
   * file on the thread pool (returns true).
   */
  #encodeQueue(syncOpen: boolean): boolean {
    const queue = this.#queue;
    const ctxs = this.#ctxs;
    if (!queue.length) return false;
    // Chunks are sized from what is left of the batch (mostly ASCII: one byte per character), at most 1 MiB each.
    let remainingChars = this.#queuedChars;
    this.#queue = [];
    this.#ctxs = [];
    this.#queuedChars = 0;

    let seg: Segment | undefined;
    let off = 0;
    let i = 0;
    const alloc = (file: DataFile, line: string): Segment => {
      const left = remainingChars + 64 * (queue.length - i) + 4096;
      const size = Math.max(Math.min(CHUNK, left), Buffer.byteLength(line));
      return { file, buf: Buffer.allocUnsafe(size), len: 0, pos: file.size, firstSeq: this.#nextSeq, records: 0 };
    };
    const finish = (): void => {
      if (seg && (off > 0 || seg.records > 0)) {
        seg.len = off;
        this.#segments.push(seg);
        if (seg.file === this.#file) this.#file.size = seg.pos + off;
      }
      seg = undefined;
      off = 0;
    };
    const put = (line: string): void => {
      if (seg === undefined) {
        seg = alloc(this.#file!, line);
      } else if (seg.buf.length - off < line.length * 3 && seg.buf.length - off < Buffer.byteLength(line)) {
        const file = seg.file;
        finish();
        seg = alloc(file, line);
      }
      off += seg.buf.write(line, off, "utf8");
    };

    for (; i < queue.length; i++) {
      remainingChars -= queue[i]!.length;
      if (this.#spoolFull) {
        this.#droppedSpoolFull++;
        continue;
      }
      const size = this.#file ? this.#file.size + (seg ? off : 0) : 0;
      if (!this.#file || size >= this.#fileMaxBytes) {
        finish();
        this.#retire();
        if (this.#openFailed) {
          // Opening the next file failed: this batch is dropped (counted); the next batch tries again.
          this.#openFailed = false;
          this.#droppedWriteError += queue.length - i;
          break;
        }
        if (!syncOpen) {
          this.#queue = queue.slice(i);
          this.#ctxs = ctxs.slice(i);
          this.#queuedChars = remainingChars + queue[i]!.length;
          return true;
        }
        if (!this.#openSync()) {
          this.#droppedWriteError++;
          continue;
        }
      }
      if (this.#needHeader) {
        this.#needHeader = false;
        put(this.#header);
      }
      const ctx = ctxs[i] ?? this.root;
      let ref = this.#refs.get(ctx.id);
      if (ref === undefined) ref = this.#defineContext(ctx, put);
      put('{"type":"record","seq":"' + this.#nextSeq + '","context":' + ref + "," + queue[i] + "}\n");
      this.#nextSeq++;
      seg!.records++;
    }
    finish();
    return false;
  }

  /** File-local ref of `ctx`, writing the context lines of it and of any ancestor not yet defined in this file. */
  #defineContext(ctx: Context, put: (line: string) => void): number {
    const chain: Context[] = [];
    for (let c: Context | undefined = ctx; c && !this.#refs.has(c.id); c = c.parent) chain.push(c);
    let r = 0;
    for (let i = chain.length - 1; i >= 0; i--) {
      const x = chain[i]!;
      const parent = x.parent ? this.#refs.get(x.parent.id) : undefined;
      r = this.#nextRef++;
      this.#refs.set(x.id, r);
      put('{"type":"context","ref":' + r + (parent !== undefined ? ',"parent":' + parent : "") + "," + x.def + "}\n");
    }
    return r;
  }

  /**
   * Retires the current file: fsynced on the thread pool (a closed file is always durable, retention may delete it
   * later), then closed. Retention runs after each rotation.
   */
  #retire(): void {
    const old = this.#file;
    if (!old) return;
    this.#file = undefined;
    this.#dirty = false;
    this.#syncPending++;
    this.#syncTail = this.#syncTail
      .then(() => this.#closeFile(old, true))
      .finally(() => {
        this.#syncPending--;
      });
    this.#retention();
  }

  /** Makes `f` the current file: `latticelog-<next seq>.jsonl`, starting with its own header and contexts. */
  #adopt(f: DataFile): void {
    this.#file = f;
    this.#refs.clear();
    this.#nextRef = 1;
    this.#needHeader = true;
  }

  #nextPath(): string {
    return path.join(this.processDir, fileName(this.#nextSeq));
  }

  #openSync(): boolean {
    const file = this.#nextPath();
    try {
      this.#adopt(DataFile.openSync(file));
      return true;
    } catch (e) {
      this.#error = `open ${file}: ${(e as Error).message}`;
      return false;
    }
  }

  async #openNext(): Promise<void> {
    const file = this.#nextPath();
    try {
      const f = await DataFile.open(file);
      if (this.#file) await f.close(); // opened meanwhile by a synchronous drain
      else this.#adopt(f);
    } catch (e) {
      this.#error = `open ${file}: ${(e as Error).message}`;
      this.#openFailed = true;
    }
  }

  async #closeFile(f: DataFile, sync: boolean): Promise<void> {
    // Wait for the segments of this file that are still being written.
    if (sync) {
      try {
        await this.#waitWrites(f);
        await f.datasync();
      } catch (e) {
        this.#fsyncError = `fsync: ${(e as Error).message}`;
      }
    }
    await f.close();
  }

  #writeDone: Array<() => void> = [];
  async #waitWrites(f: DataFile): Promise<void> {
    while (this.#segments.slice(this.#segIndex).some((s) => s.file === f)) {
      await new Promise<void>((r) => this.#writeDone.push(r));
    }
  }

  /** Writes the pending segments in order. A failed write cuts the file back and drops the rest of the batch. */
  async #writeSegments(): Promise<void> {
    while (this.#segIndex < this.#segments.length) {
      const s = this.#segments[this.#segIndex]!;
      try {
        await s.file.write(s.buf, s.len, s.pos);
        this.#dirty = true;
        this.#written += s.records;
        if (s.records) this.#lastSeq = s.firstSeq + s.records - 1;
        this.#segIndex++;
      } catch (e) {
        this.#failSegments(s, e as Error);
      }
      for (const f of this.#writeDone.splice(0)) f();
    }
    this.#segments = [];
    this.#segIndex = 0;
  }

  /**
   * The file is cut back to the start of the failed segment, its records and every later one of this batch are
   * counted as dropped and their sequences are reused, so the folder never has a gap or a duplicate. The next record
   * starts a new file.
   */
  #failSegments(failed: Segment, e: Error): void {
    this.#error = `write: ${e.message}`;
    let dropped = 0;
    for (let i = this.#segIndex; i < this.#segments.length; i++) dropped += this.#segments[i]!.records;
    this.#droppedWriteError += dropped;
    try {
      fs.ftruncateSync(failed.file.fd, failed.pos);
    } catch {
      // the next file starts after the damage; the reader keeps a malformed line as text
    }
    this.#nextSeq = failed.firstSeq;
    const files = new Set(this.#segments.slice(this.#segIndex).map((s) => s.file));
    this.#segIndex = this.#segments.length;
    for (const f of files) {
      if (f === this.#file) this.#file = undefined;
      if (f !== failed.file) {
        // Opened by a rotation of this batch and never written: remove it, or its name (a later sequence) would
        // look like the newest file.
        try {
          if (fs.fstatSync(f.fd).size === 0) fs.unlinkSync(f.path);
        } catch {
          // best effort
        }
      }
      this.#syncs.push(f.close());
    }
  }

  /** Writes the queue from the calling thread (saturation "block", and at exit). */
  #drainSync(): void {
    // Segments still in flight on the thread pool are written again at the same positions (same bytes): when the
    // process exits right after, their asynchronous write may never complete.
    for (let i = this.#segIndex; i < this.#segments.length; i++) {
      const s = this.#segments[i]!;
      this.#writeSync(s);
    }
    const base = this.#segments.length;
    this.#encodeQueue(true);
    for (let i = base; i < this.#segments.length; i++) {
      const s = this.#segments[i]!;
      if (!this.#writeSync(s)) break;
      this.#written += s.records;
      if (s.records) this.#lastSeq = s.firstSeq + s.records - 1;
      this.#dirty = true;
    }
    if (!this.#writing) {
      this.#segments = [];
      this.#segIndex = 0;
    } else {
      // The writer loop is awaiting a write: let it find its own segments again (rewritten, idempotent), and skip
      // the ones written here.
      this.#segments.length = base;
    }
  }

  #writeSync(s: Segment): boolean {
    if (s.file.closed) {
      this.#droppedWriteError += s.records;
      return false;
    }
    try {
      let off = 0;
      while (off < s.len) off += fs.writeSync(s.file.fd, s.buf, off, s.len - off, s.pos + off);
      return true;
    } catch (e) {
      this.#error = `write: ${(e as Error).message}`;
      this.#droppedWriteError += s.records;
      return false;
    }
  }

  /** @internal Process exit: write what is queued (and fsync in Durable mode), release the process folder. */
  _exitSync(): void {
    if (this.#exited) return;
    this.#exited = true;
    this.#closed = true; // records from later exit handlers are counted in droppedAfterClose
    this.#cancelWake();
    this.#cancelFsyncTimer();
    if (!this.#closing || this.#queue.length || this.#segIndex < this.#segments.length) {
      try {
        this.#drainSync();
        if (this.#durability === "durable" && this.#file) fs.fdatasyncSync(this.#file.fd);
      } catch {
        // nothing more can be done at exit
      }
    }
    releaseLock(this.processDir, this.#lock);
  }

  #retention(): void {
    if (this.#maxAgeMs === undefined && this.#maxBytes === undefined) return;
    this.#background = this.#background.then(() => applyRetention(this.folder, this.#maxAgeMs, this.#maxBytes)).catch(() => undefined);
  }

  // --------------------------------------------------------------------------------------------- public control

  /**
   * Waits until every record accepted before the call is handed to the OS, and fsynced when `fsync` is true (always
   * in Durable mode). Reports the level actually reached and any write error since the previous flush.
   */
  flush(options?: { fsync?: boolean }): Promise<FlushResult> {
    if (this.#exited) return Promise.resolve({ level: "None", lastSeq: this.#lastSeq, error: "process exiting" });
    return new Promise<FlushResult>((resolve) => {
      this.#flushWaiters.push({ fsync: options?.fsync ?? false, resolve });
      if (!this.#writing) {
        this.#cancelWake();
        void this.#run();
      }
    });
  }

  /** Server mode: flushes with fsync, then waits until the server confirmed every record written so far. */
  async flushShipped(timeoutMs = 30_000): Promise<boolean> {
    const r = await this.flush({ fsync: true });
    return this.#shipper ? this.#shipper.waitFor(r.lastSeq, timeoutMs) : true;
  }

  stats(): Stats {
    const s: Stats = {
      written: this.#written,
      lastSeq: this.#lastSeq,
      queued: this.#queue.length,
      droppedSaturation: this.#droppedSaturation,
      droppedWriteError: this.#droppedWriteError,
      droppedSpoolFull: this.#droppedSpoolFull,
      droppedAfterClose: this.#droppedAfterClose,
      committedThrough: 0,
      batchesShipped: 0,
      shipRetries: 0,
      shipFailed: false,
    };
    this.#shipper?.fillStats(s);
    return s;
  }

  /**
   * Writes everything accepted, fsyncs, (server mode) waits up to `closeTimeoutMs` for the server, then releases the
   * process folder. Later records are counted in `droppedAfterClose`. Idempotent.
   */
  close(): Promise<void> {
    this.#closing ??= this.#close();
    return this.#closing;
  }

  async #close(): Promise<void> {
    const r = await this.flush({ fsync: true });
    this.#closed = true;
    await this.flush({ fsync: true }); // records that raced with the first flush
    if (this.#shipper) {
      await this.#shipper.waitFor(r.lastSeq, this.#closeTimeoutMs);
      await this.#shipper.stop();
    }
    this.#cancelWake();
    this.#cancelFsyncTimer();
    if (this.#file) {
      const f = this.#file;
      this.#file = undefined;
      await f.close();
    }
    await Promise.all([...this.#syncs.splice(0), this.#syncTail]);
    await this.#background;
    if (!this.#exited) releaseLock(this.processDir, this.#lock);
    exitHooks.delete(this);
  }

  /** `await using log = open(…)` closes the logger at the end of the block. */
  [Symbol.asyncDispose](): Promise<void> {
    return this.close();
  }
}

export function levelNumber(level: LevelName | number | string): number {
  if (typeof level === "number") return Math.min(24, Math.max(1, Math.round(level)));
  const lower = level.toLowerCase();
  if (Object.hasOwn(Level, lower)) return (Level as Record<string, number>)[lower]!;
  switch (level.toLowerCase()) {
    case "warning":
      return Level.warn;
    case "critical":
    case "crit":
    case "emerg":
    case "alert":
    case "panic":
      return Level.fatal;
    case "err":
      return Level.error;
    case "information":
    case "notice":
    case "http":
      return Level.info;
    case "verbose":
      return 6;
    case "silly":
      return Level.trace;
  }
  throw new RangeError(`unknown level ${JSON.stringify(level)}`);
}

/**
 * Opens (or creates) the source folder of one service and starts writing.
 *
 * Records become official `latticelog.jsonl` v1 lines in `<folder>/p-N/latticelog-<seq>.jsonl`, next to the manifest
 * `latticelog.source.json`. Register the parent folder once (`latticelog roots add logs`) and the CLI and MCP server
 * find the service by name. Several processes may share the folder: each writes its own `p-N`. Opening does
 * synchronous file I/O (manifest, process folder, recovery of a torn last line); logging calls never do.
 */
export function open(folder: string, options: OpenOptions): Logger {
  if (!options || typeof options.service !== "string" || options.service === "") throw new TypeError("service is required");
  const abs = path.resolve(folder);
  fs.mkdirSync(abs, { recursive: true });
  ensureManifest(abs, options.service, options.environment);
  const pf = acquireProcessFolder(abs);
  return new Logger(abs, options, pf);
}

export { PRODUCER };
