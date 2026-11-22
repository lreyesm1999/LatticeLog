// Server mode: ships the process folder to latticelog-server with ingest protocol v1 (specs/ingest-protocol.md).
//
// The files the writer produces are already latticelog.jsonl documents, so a batch is the file's own record lines,
// byte for byte, behind a header and the context lines they reference: nothing is decoded or re-encoded. Only the
// server's acknowledgement moves `cursor.json`; nothing unconfirmed is ever deleted.

import * as fs from "node:fs";
import * as fsp from "node:fs/promises";
import * as path from "node:path";
import * as zlib from "node:zlib";
import { randomInt } from "node:crypto";
import { type ProcessFolder, atomicWrite, atomicWriteSync, committedThrough, dataFilesAsync } from "./folder.js";

/** Where and how to ship. */
export interface ServerOptions {
  /** Base URL of latticelog-server, e.g. `https://logs:7400`. */
  endpoint: string;
  /** Bearer token with the ingest scope. */
  token: string;
  /** Target namespace (`LatticeLog-Namespace` header). */
  namespace?: string;
  /** Persistence level the server must reach before it acknowledges: `fsync` (default) or `os`. */
  ack?: "fsync" | "os";
  /** Records per request (default 5,000; the server accepts up to 50,000). */
  batchRecords?: number;
  /** Uncompressed bytes per request (default 4 MiB; the server's limits are 8 MiB sent and 64 MiB decoded). */
  batchBytes?: number;
  /** How long a partial batch may wait for more records (default 100 ms). `flushShipped` skips it. */
  lingerMs?: number;
  /** How often the folder is checked for new records when the writer is quiet (default 200 ms). */
  pollIntervalMs?: number;
  /** Unconfirmed backlog at which new records are refused and counted (`droppedSpoolFull`). Default 512 MiB. */
  maxSpoolBytes?: number;
  /** Keep confirmed files (then `maxAgeMs`/`maxBytes` govern them) instead of deleting them. */
  keepAcknowledged?: boolean;
  /** Exponential backoff with full jitter between these bounds (defaults 200 ms and 30 s). */
  retryBaseMs?: number;
  retryMaxMs?: number;
  /** Timeout of one request, including the response (default 30 s). */
  timeoutMs?: number;
}

export interface ShipStats {
  /** Server mode: highest sequence the server confirmed. */
  committedThrough: number;
  /** Server mode: batches confirmed, retries, and the last shipping error. */
  batchesShipped: number;
  shipRetries: number;
  shipError?: string;
  /** Server mode: true when shipping stopped for good (authentication, invalid batch: see `shipError`). */
  shipFailed: boolean;
}

// ------------------------------------------------------------------------------------------------- CRC-32

let crcTable: Int32Array | undefined;
/** CRC-32 (IEEE); `zlib.crc32` when the runtime has it (Node ≥ 20.15 / 22.2). */
export function crc32(data: Uint8Array): number {
  const native = (zlib as unknown as { crc32?: (d: Uint8Array) => number }).crc32;
  if (typeof native === "function") return native(data) >>> 0;
  if (!crcTable) {
    crcTable = new Int32Array(256);
    for (let n = 0; n < 256; n++) {
      let c = n;
      for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
      crcTable[n] = c;
    }
  }
  let c = -1;
  for (let i = 0; i < data.length; i++) c = crcTable[(c ^ data[i]!) & 0xff]! ^ (c >>> 8);
  return (c ^ -1) >>> 0;
}

function gzip(data: Buffer): Promise<Buffer> {
  return new Promise((resolve, reject) => zlib.gzip(data, { level: 1 }, (e, out) => (e ? reject(e) : resolve(out))));
}

/** The 48-byte `LLIB` envelope (little-endian) followed by the gzip body. */
export async function envelope(producer: Buffer, first: number, count: number, gap: boolean, body: Buffer): Promise<Buffer> {
  const payload = await gzip(body);
  const h = Buffer.alloc(48);
  h.write("LLIB", 0, "latin1");
  h[4] = 1; // version
  h[5] = 1; // body_encoding: latticelog.jsonl v1
  h[6] = 1; // compression: gzip
  h[7] = gap ? 1 : 0; // flags: gap_acknowledged
  producer.copy(h, 8, 0, 16);
  h.writeBigUInt64LE(BigInt(first), 24);
  h.writeBigUInt64LE(BigInt(first + count - 1), 32);
  h.writeUInt32LE(count, 40);
  h.writeUInt32LE(crc32(payload), 44);
  return Buffer.concat([h, payload]);
}

// ------------------------------------------------------------------------------------------------- reading

interface Batch {
  first: number;
  count: number;
  gap: boolean;
  body: Buffer;
}

const RECORD = Buffer.from('{"type":"record","seq":"');
const CONTEXT = Buffer.from('{"type":"context","ref":');
const CTX_KEY = Buffer.from(',"context":');
const PARENT_KEY = Buffer.from(',"parent":');

function startsWith(buf: Buffer, at: number, end: number, prefix: Buffer): boolean {
  return end - at >= prefix.length && buf.compare(prefix, 0, prefix.length, at, at + prefix.length) === 0;
}

function digits(buf: Buffer, at: number, end: number): [number, number] {
  let n = 0;
  let i = at;
  while (i < end && buf[i]! >= 0x30 && buf[i]! <= 0x39) n = n * 10 + (buf[i++]! - 0x30);
  return [i > at ? n : -1, i];
}

/** `[seq, context ref]` of a record line, or null. */
function parseRecord(buf: Buffer, at: number, end: number): [number, number] | null {
  if (!startsWith(buf, at, end, RECORD)) return null;
  const [seq, i] = digits(buf, at + RECORD.length, end);
  if (seq < 0 || buf[i] !== 0x22) return null;
  if (!startsWith(buf, i + 1, end, CTX_KEY)) return [seq, -1];
  return [seq, digits(buf, i + 1 + CTX_KEY.length, end)[0]];
}

/** `[ref, parent ref or -1]` of a context line, or null. */
function parseContext(buf: Buffer, at: number, end: number): [number, number] | null {
  if (!startsWith(buf, at, end, CONTEXT)) return null;
  const [ref, i] = digits(buf, at + CONTEXT.length, end);
  if (ref < 0) return null;
  return [ref, startsWith(buf, i, end, PARENT_KEY) ? digits(buf, i + PARENT_KEY.length, end)[0] : -1];
}

interface Cursor {
  firstSeq: number;
  file: string;
  fh: fsp.FileHandle;
  buf: Buffer;
  bufStart: number;
  contexts: Map<number, { parent: number; line: Buffer }>;
}

/** Incremental reader of the process folder's files: keeps the bytes from the first unconfirmed record on. */
class Reader {
  #cur: Cursor | undefined;
  readonly #header: Buffer;

  constructor(
    readonly dir: string,
    producerHex: string,
  ) {
    this.#header = Buffer.from(`{"type":"header","format":"latticelog.jsonl","version":1,"store_id":"${producerHex}"}\n`);
  }

  async reset(): Promise<void> {
    const c = this.#cur;
    this.#cur = undefined;
    await c?.fh.close().catch(() => undefined);
  }

  /** Opens the file that holds `committed + 1` (or the oldest file when that one is gone). */
  async #openFor(committed: number): Promise<boolean> {
    const files = await dataFilesAsync(this.dir);
    let pick = files.length ? files[0] : undefined;
    for (let i = files.length - 1; i >= 0; i--) {
      if (files[i]![0] <= committed + 1) {
        pick = files[i];
        break;
      }
    }
    if (!pick) return false;
    this.#cur = { firstSeq: pick[0], file: pick[1], fh: await fsp.open(pick[1], "r"), buf: Buffer.alloc(0), bufStart: 0, contexts: new Map() };
    return true;
  }

  async nextBatch(committed: number, maxRecords: number, maxBytes: number): Promise<Batch | null> {
    for (let guard = 0; guard < 1000; guard++) {
      if (!this.#cur && !(await this.#openFor(committed))) return null;
      const cur = this.#cur!;
      const eof = cur.bufStart + cur.buf.length;
      const size = (await cur.fh.stat()).size;
      if (size > eof) {
        const chunk = Buffer.allocUnsafe(Math.min(size - eof, 64 * 1024 * 1024));
        const { bytesRead } = await cur.fh.read(chunk, 0, chunk.length, eof);
        cur.buf = cur.buf.length ? Buffer.concat([cur.buf, chunk.subarray(0, bytesRead)]) : chunk.subarray(0, bytesRead);
      }

      const buf = cur.buf;
      let pos = 0;
      let dropTo = 0;
      const records: Array<[number, number]> = [];
      const needed = new Set<number>();
      let first = 0;
      let last = 0;
      let bytes = 0;
      let pendingAfter = false; // unconfirmed records remain in this file beyond the batch
      for (;;) {
        const nl = buf.indexOf(0x0a, pos);
        if (nl < 0) break;
        const end = nl + 1;
        const ctx = parseContext(buf, pos, nl);
        if (ctx) {
          cur.contexts.set(ctx[0], { parent: ctx[1], line: Buffer.from(buf.subarray(pos, end)) });
          if (!records.length) dropTo = end;
        } else {
          const rec = parseRecord(buf, pos, nl);
          if (rec) {
            const [seq, ref] = rec;
            if (seq <= committed) {
              if (!records.length) dropTo = end;
            } else if (!records.length || (seq === last + 1 && records.length < maxRecords && bytes + (end - pos) < maxBytes)) {
              if (!records.length) first = seq;
              last = seq;
              bytes += end - pos;
              records.push([pos, end]);
              if (ref >= 0) needed.add(ref);
            } else {
              pendingAfter = true;
              break;
            }
          } else if (!records.length) {
            dropTo = end; // header or a line we do not ship
          }
        }
        pos = end;
      }

      if (records.length) {
        // Contexts referenced by the records, with their ancestors, in definition order.
        const all = new Set<number>();
        for (const r of needed) {
          for (let x = r; x >= 0 && !all.has(x); x = cur.contexts.get(x)?.parent ?? -1) all.add(x);
        }
        const parts: Buffer[] = [this.#header];
        for (const r of [...all].sort((a, b) => a - b)) {
          const c = cur.contexts.get(r);
          if (c) parts.push(c.line);
        }
        for (const [s, e] of records) parts.push(buf.subarray(s, e));
        const body = Buffer.concat(parts);
        cur.buf = buf.subarray(dropTo);
        cur.bufStart += dropTo;
        return { first, count: records.length, gap: first !== committed + 1, body };
      }

      cur.buf = buf.subarray(dropTo);
      cur.bufStart += dropTo;
      if (pendingAfter || cur.buf.length) return null; // a partial line is being written
      // Everything in this file is confirmed. Move on only when a newer file exists (this one is closed).
      const files = await dataFilesAsync(this.dir);
      if (!files.some(([f]) => f > cur.firstSeq)) return null;
      // Read once more: the writer finished this file before creating the next one.
      if ((await cur.fh.stat()).size > cur.bufStart) continue;
      await this.reset();
    }
    return null;
  }
}

/** Deletes closed files whose records are all confirmed. */
async function deleteConfirmed(dir: string, committed: number): Promise<void> {
  const files = await dataFilesAsync(dir).catch(() => []);
  for (let i = 0; i + 1 < files.length; i++) {
    if (files[i + 1]![0] - 1 <= committed) await fsp.unlink(files[i]![1]).catch(() => undefined);
  }
}

/** Bytes of files that still hold unconfirmed records. */
async function unconfirmedBytes(dir: string, committed: number): Promise<number> {
  const files = await dataFilesAsync(dir).catch(() => []);
  let total = 0;
  for (let i = 0; i < files.length; i++) {
    const last = i + 1 < files.length ? files[i + 1]![0] - 1 : Infinity;
    if (last > committed) total += (await fsp.stat(files[i]![1]).catch(() => ({ size: 0 }))).size;
  }
  return total;
}

// ------------------------------------------------------------------------------------------------- shipper

type Reply = { kind: "http"; status: number; body: Record<string, unknown>; retryAfter?: number } | { kind: "network"; error: string };

/** `AbortSignal.any` (Node ≥ 20.3), or a hand-made combination. */
function anySignal(a: AbortSignal, b: AbortSignal): AbortSignal {
  const any = (AbortSignal as unknown as { any?: (s: AbortSignal[]) => AbortSignal }).any;
  if (typeof any === "function") return any([a, b]);
  const c = new AbortController();
  const abort = (): void => c.abort();
  a.addEventListener("abort", abort, { once: true });
  b.addEventListener("abort", abort, { once: true });
  return c.signal;
}

function parseRetryAfter(v: string | null): number | undefined {
  if (!v) return undefined;
  const s = Number(v.trim());
  if (Number.isFinite(s)) return s;
  const d = Date.parse(v);
  return Number.isFinite(d) ? Math.max(0, (d - Date.now()) / 1000) : undefined;
}

/** @internal The background shipper of one process folder. */
export class Shipper {
  readonly #opts: Required<Omit<ServerOptions, "namespace">> & { namespace: string | undefined };
  readonly #dir: string;
  readonly #producer: Buffer;
  readonly #setSpoolFull: (full: boolean) => void;
  #committed: number;
  #written = false;
  /** Callers waiting in `waitFor`: a partial batch does not linger while someone waits. */
  #urgent = 0;
  #stopped = false;
  #wake: (() => void) | undefined;
  #loop: Promise<void> | undefined;
  #abort = new AbortController();
  #ackWaiters: Array<() => void> = [];

  #batches = 0;
  #retries = 0;
  #error: string | undefined;
  #failed = false;

  constructor(opts: ServerOptions, pf: ProcessFolder, setSpoolFull: (full: boolean) => void) {
    if (!opts.endpoint || !opts.token) throw new TypeError("server mode needs endpoint and token");
    this.#opts = {
      endpoint: opts.endpoint,
      token: opts.token,
      namespace: opts.namespace,
      ack: opts.ack ?? "fsync",
      batchRecords: Math.min(50_000, Math.max(1, opts.batchRecords ?? 5000)),
      batchBytes: Math.min(48 * 1024 * 1024, Math.max(4096, opts.batchBytes ?? 4 * 1024 * 1024)),
      lingerMs: opts.lingerMs ?? 100,
      pollIntervalMs: opts.pollIntervalMs ?? 200,
      maxSpoolBytes: opts.maxSpoolBytes ?? 512 * 1024 * 1024,
      keepAcknowledged: opts.keepAcknowledged ?? false,
      retryBaseMs: opts.retryBaseMs ?? 200,
      retryMaxMs: opts.retryMaxMs ?? 30_000,
      timeoutMs: opts.timeoutMs ?? 30_000,
    };
    this.#dir = pf.dir;
    this.#producer = pf.producerId;
    this.#setSpoolFull = setSpoolFull;
    this.#committed = committedThrough(pf.dir);
    // A cursor from the start marks the folder as shipped: retention never deletes a closed file the server has not
    // confirmed (specs/local-sources.md §1, Retention).
    if (!fs.existsSync(path.join(pf.dir, "cursor.json"))) atomicWriteSync(path.join(pf.dir, "cursor.json"), '{"committed_through":0}');
  }

  start(): void {
    this.#loop = this.#run().catch((e: Error) => {
      this.#error = `shipper stopped: ${e.message}`;
      this.#failed = true;
      this.#notifyAck();
    });
  }

  #signal(): void {
    const w = this.#wake;
    this.#wake = undefined;
    w?.();
  }

  notifyWritten(): void {
    this.#written = true;
    this.#signal();
  }

  #notifyAck(): void {
    for (const w of this.#ackWaiters.splice(0)) w();
  }

  /** Waits until the server confirmed everything through `seq`. False on timeout or when shipping failed. */
  async waitFor(seq: number, timeoutMs: number): Promise<boolean> {
    const deadline = Date.now() + timeoutMs;
    this.#urgent++;
    this.#signal();
    try {
      for (;;) {
        if (this.#committed >= seq) return true;
        const left = deadline - Date.now();
        if (this.#failed || this.#stopped || left <= 0) return false;
        // Every wait holds its own referenced timer: the shipper's timers are unref'd, so without one the event loop
        // could empty while the caller awaits and the process would exit with the promise unsettled. A timer may
        // fire a millisecond before `Date.now()` reaches the deadline; the next turn of the loop arms a new one.
        await new Promise<void>((resolve) => {
          const keepAlive = setTimeout(resolve, left);
          this.#ackWaiters.push(() => {
            clearTimeout(keepAlive);
            resolve();
          });
        });
      }
    } finally {
      this.#urgent--;
    }
  }

  async stop(): Promise<void> {
    this.#stopped = true;
    this.#abort.abort();
    this.#signal();
    await this.#loop;
    this.#notifyAck();
  }

  fillStats(s: ShipStats): void {
    s.committedThrough = this.#committed;
    s.batchesShipped = this.#batches;
    s.shipRetries = this.#retries;
    if (this.#error !== undefined) s.shipError = this.#error;
    s.shipFailed = this.#failed;
  }

  /** Waits for new records, the timeout or a stop. With `lingering` (a partial batch waiting for more), new records
   * do not end the wait early, but a caller of `waitFor` does. */
  async #wait(timeoutMs: number, lingering: boolean): Promise<boolean> {
    const deadline = Date.now() + timeoutMs;
    for (;;) {
      if (this.#stopped) return false;
      if (lingering ? this.#urgent > 0 : this.#written) {
        this.#written = false;
        return true;
      }
      const left = deadline - Date.now();
      if (left <= 0) {
        this.#written = false;
        return true;
      }
      await new Promise<void>((resolve) => {
        const t = setTimeout(resolve, left);
        t.unref(); // the folder is the outbox: an idle shipper never keeps the process alive
        this.#wake = () => {
          clearTimeout(t);
          resolve();
        };
      });
      this.#wake = undefined;
    }
  }

  async #backoff(attempt: number, retryAfter?: number): Promise<void> {
    const cap = Math.min(this.#opts.retryMaxMs, this.#opts.retryBaseMs * 2 ** Math.min(attempt, 20));
    let delay = randomInt(0, Math.max(1, Math.floor(cap)) + 1); // full jitter
    if (retryAfter !== undefined && Number.isFinite(retryAfter) && retryAfter > 0) delay = Math.max(delay, Math.min(retryAfter, 3600) * 1000);
    const deadline = Date.now() + delay;
    while (!this.#stopped && Date.now() < deadline) {
      await new Promise<void>((resolve) => {
        const t = setTimeout(resolve, deadline - Date.now());
        t.unref();
        this.#wake = () => {
          clearTimeout(t);
          resolve();
        };
      });
      this.#wake = undefined;
    }
  }

  async #acknowledge(committed: number): Promise<void> {
    try {
      await atomicWrite(path.join(this.#dir, "cursor.json"), `{"committed_through":${committed}}`);
    } catch (e) {
      this.#error = `cursor.json: ${(e as Error).message}`;
    }
    this.#committed = committed;
    this.#notifyAck();
  }

  #report(error: string | undefined, retry: boolean, failed: boolean): void {
    this.#error = error;
    if (retry) this.#retries++;
    if (failed) {
      this.#failed = true;
      this.#notifyAck();
    }
  }

  async #post(url: string, body: Buffer): Promise<Reply> {
    const headers: Record<string, string> = {
      Authorization: `Bearer ${this.#opts.token}`,
      "Content-Type": "application/vnd.latticelog.batch",
      "LatticeLog-Ack": this.#opts.ack,
    };
    if (this.#opts.namespace !== undefined) headers["LatticeLog-Namespace"] = this.#opts.namespace;
    try {
      const r = await fetch(url, {
        method: "POST",
        headers,
        body: new Uint8Array(body.buffer, body.byteOffset, body.byteLength),
        signal: anySignal(this.#abort.signal, AbortSignal.timeout(this.#opts.timeoutMs)),
      });
      const text = await r.text();
      let parsed: Record<string, unknown> = {};
      try {
        const j = JSON.parse(text) as unknown;
        if (typeof j === "object" && j !== null) parsed = j as Record<string, unknown>;
      } catch {
        // not JSON
      }
      return { kind: "http", status: r.status, body: parsed, retryAfter: parseRetryAfter(r.headers.get("Retry-After")) };
    } catch (e) {
      const cause = (e as { cause?: { message?: string } }).cause?.message;
      return { kind: "network", error: `network: ${(e as Error).message}${cause ? ` (${cause})` : ""}` };
    }
  }

  async #run(): Promise<void> {
    const o = this.#opts;
    const url = `${o.endpoint.replace(/\/+$/, "")}/v1/ingest`;
    const reader = new Reader(this.#dir, this.#producer.toString("hex"));
    let attempt = 0;
    let maxRecords = o.batchRecords;
    let lastSizeCheck = 0;
    if (!o.keepAcknowledged) await deleteConfirmed(this.#dir, this.#committed);
    try {
      while (!this.#stopped) {
        const committed = this.#committed;
        if (Date.now() - lastSizeCheck >= 500) {
          this.#setSpoolFull((await unconfirmedBytes(this.#dir, committed)) >= o.maxSpoolBytes);
          lastSizeCheck = Date.now();
        }
        let batch: Batch | null;
        try {
          batch = await reader.nextBatch(committed, maxRecords, o.batchBytes);
        } catch (e) {
          this.#report(`reading ${this.#dir}: ${(e as Error).message}`, false, false);
          await reader.reset();
          if (!(await this.#wait(o.pollIntervalMs, false))) return;
          continue;
        }
        if (!batch) {
          if (!(await this.#wait(o.pollIntervalMs, false))) return;
          continue;
        }
        const fullBatch = batch.count >= maxRecords;
        let env: Buffer;
        try {
          env = await envelope(this.#producer, batch.first, batch.count, batch.gap, batch.body);
        } catch (e) {
          this.#report(`compression: ${(e as Error).message}`, true, false);
          await this.#backoff(++attempt);
          continue;
        }
        const reply = await this.#post(url, env);
        if (this.#stopped && reply.kind === "network") return;
        if (reply.kind === "network") {
          this.#report(reply.error, true, false);
          await this.#backoff(++attempt);
          continue;
        }
        const { status, body } = reply;
        if (status === 200) {
          attempt = 0;
          maxRecords = o.batchRecords;
          const ack = typeof body.ack === "string" ? body.ack : "";
          if (ack === "FsyncConfirmed" || o.ack === "os") {
            const c = Number(body.committed_through ?? committed);
            if (Number.isFinite(c) && c > committed) {
              await this.#acknowledge(c);
              if (!o.keepAcknowledged) await deleteConfirmed(this.#dir, c);
            }
            this.#batches++;
            this.#error = undefined;
            if (!fullBatch && this.#urgent === 0 && !(await this.#wait(o.lingerMs, true))) return;
          } else {
            this.#report(`server acknowledged only ${ack || "(nothing)"}`, true, false);
            await this.#backoff(++attempt);
          }
        } else if (status === 409 && body.error === "sequence_gap") {
          // The server's truth: resend from its cursor (or declare the gap when those records are gone).
          const c = Number(body.committed_through ?? 0);
          await this.#acknowledge(Number.isFinite(c) ? c : 0);
          await reader.reset();
        } else if (status === 413 && batch.count > 1) {
          maxRecords = Math.max(1, Math.floor(batch.count / 2));
        } else if (status === 429 || status >= 500) {
          this.#report(typeof body.error === "string" ? body.error : `HTTP ${status}`, true, false);
          await this.#backoff(++attempt, reply.retryAfter);
        } else {
          const err = typeof body.error === "string" ? body.error : "";
          const msg = typeof body.message === "string" ? body.message : "";
          this.#report(`HTTP ${status} ${err}: ${msg}`, false, true);
          return;
        }
      }
    } finally {
      await reader.reset();
    }
  }
}
