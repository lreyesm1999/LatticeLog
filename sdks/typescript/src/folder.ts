// The source folder layout shared with the Python and Rust SDKs (specs/local-sources.md §1):
//
//   <folder>/latticelog.source.json         manifest: service, environment, format version, producer id
//   <folder>/p-N/LOCK                        {"pid":…,"host":…,"started":…,"token":…} of the process writing p-N
//   <folder>/p-N/process.json                {"producer_id": "<32 hex>"}
//   <folder>/p-N/cursor.json                 {"committed_through": N} (server mode: what the server confirmed)
//   <folder>/p-N/latticelog-<seq>.jsonl      self-contained latticelog.jsonl documents named after their first seq
//
// Node.js has no portable file lock, so a process folder is claimed by creating `LOCK` exclusively (`wx`) with the
// owner's pid, host and start time. A LOCK whose process is gone is stale and taken over; see `isStale` for the rules
// and README "Limitations" for what this cannot detect.

import * as fs from "node:fs";
import * as fsp from "node:fs/promises";
import { randomBytes } from "node:crypto";
import { hostname } from "node:os";
import * as path from "node:path";

export const MANIFEST = "latticelog.source.json";
export const PRODUCER = "latticelog-typescript";
const PREFIX = "latticelog-";
const SUFFIX = ".jsonl";
const FILE_RE = /^latticelog-(\d{20})\.jsonl$/;
const DIR_RE = /^p-(\d+)$/;

const sleepCell = new Int32Array(new SharedArrayBuffer(4));
/** Synchronous sleep (only used at open, while another process is between creating a file and writing it). */
function sleepSync(ms: number): void {
  Atomics.wait(sleepCell, 0, 0, ms);
}

function invalid(msg: string): Error {
  return Object.assign(new Error(msg), { code: "LATTICELOG_INVALID" });
}

export function randomId(): Buffer {
  for (;;) {
    const b = randomBytes(16);
    if (b.some((x) => x !== 0)) return b; // the ingest protocol forbids an all-zero producer id
  }
}

export function fileName(firstSeq: number): string {
  return PREFIX + String(firstSeq).padStart(20, "0") + SUFFIX;
}

/** Writes `text` to `file` through a fsynced temporary file and a rename. */
export function atomicWriteSync(file: string, text: string): void {
  const tmp = file + ".tmp";
  const fd = fs.openSync(tmp, "w");
  try {
    fs.writeSync(fd, text);
    fs.fsyncSync(fd);
  } finally {
    fs.closeSync(fd);
  }
  fs.renameSync(tmp, file);
}

export async function atomicWrite(file: string, text: string): Promise<void> {
  const tmp = file + ".tmp";
  const h = await fsp.open(tmp, "w");
  try {
    await h.writeFile(text);
    await h.sync();
  } finally {
    await h.close();
  }
  await fsp.rename(tmp, file);
}

/** Creates the manifest once (exclusive create: two processes starting together agree on one) or checks it. */
export function ensureManifest(folder: string, service: string, environment: string | undefined): void {
  const file = path.join(folder, MANIFEST);
  for (let attempt = 0; attempt < 50; attempt++) {
    let text: string;
    try {
      text = fs.readFileSync(file, "utf8");
    } catch (e) {
      if ((e as NodeJS.ErrnoException).code !== "ENOENT") throw e;
      const manifest = {
        format: "latticelog.source",
        version: 1,
        service,
        environment: environment ?? null,
        data_format: "latticelog.jsonl",
        data_version: 1,
        producer_id: randomId().toString("hex"),
        layout: "process-folders",
        created: new Date().toISOString().slice(0, 19) + "Z",
        producer: PRODUCER,
      };
      let fd: number;
      try {
        fd = fs.openSync(file, "wx");
      } catch (e2) {
        if ((e2 as NodeJS.ErrnoException).code === "EEXIST") continue;
        throw e2;
      }
      try {
        fs.writeSync(fd, JSON.stringify(manifest));
        fs.fsyncSync(fd);
      } finally {
        fs.closeSync(fd);
      }
      return;
    }
    if (text.trim() === "") {
      sleepSync(10); // another process is between its exclusive create and its write
      continue;
    }
    let m: Record<string, unknown>;
    try {
      m = JSON.parse(text) as Record<string, unknown>;
    } catch (e) {
      throw invalid(`${file}: ${(e as Error).message}`);
    }
    if (m.format !== "latticelog.source" || m.version !== 1) throw invalid(`${file}: not a latticelog.source v1 manifest`);
    if (m.service !== service) {
      throw invalid(`${folder} belongs to service ${JSON.stringify(m.service)}, not ${JSON.stringify(service)}; use one folder per service`);
    }
    if (environment !== undefined && m.environment !== environment) {
      m.environment = environment;
      atomicWriteSync(file, JSON.stringify(m));
    }
    return;
  }
  throw invalid(`${file}: manifest is still empty`);
}

/** `p-N` subfolders, sorted by N. */
export function processFolders(folder: string): Array<[number, string]> {
  const out: Array<[number, string]> = [];
  for (const e of fs.readdirSync(folder, { withFileTypes: true })) {
    const m = DIR_RE.exec(e.name);
    if (m && e.isDirectory()) out.push([Number(m[1]), path.join(folder, e.name)]);
  }
  return out.sort((a, b) => a[0] - b[0]);
}

function parseDataFiles(dir: string, names: string[]): Array<[number, string]> {
  const out: Array<[number, string]> = [];
  for (const n of names) {
    const m = FILE_RE.exec(n);
    if (m) out.push([Number(m[1]), path.join(dir, n)]);
  }
  return out.sort((a, b) => a[0] - b[0]);
}

/** Data files of a process folder, sorted by first sequence. */
export function dataFiles(dir: string): Array<[number, string]> {
  return parseDataFiles(dir, fs.readdirSync(dir));
}

export async function dataFilesAsync(dir: string): Promise<Array<[number, string]>> {
  return parseDataFiles(dir, await fsp.readdir(dir));
}

// ------------------------------------------------------------------------------------------------- locking

const HOST = hostname();
/** When this process started (Unix ms), the same in every worker thread of the process. */
const PROCESS_STARTED = Math.round(Date.now() - process.uptime() * 1000);
/** Tolerance when comparing start times computed at different moments (clock adjustments, rounding). */
const START_TOLERANCE_MS = 2000;
/** A takeover in progress whose process died is ignored after this long. */
const STEAL_GUARD_STALE_MS = 10_000;

interface LockInfo {
  pid?: unknown;
  host?: unknown;
  started?: unknown;
  token?: unknown;
}

function processAlive(pid: number): boolean {
  try {
    process.kill(pid, 0);
    return true;
  } catch (e) {
    return (e as NodeJS.ErrnoException).code === "EPERM"; // exists, owned by someone else
  }
}

/**
 * True when the LOCK's owner is certainly gone. Anything uncertain counts as alive, which costs at most a new
 * `p-N` folder, never two writers in one folder:
 * - empty or unreadable content (written by the Python or Rust SDK, which hold an OS lock instead) → alive;
 * - another host (a shared network folder: its pids mean nothing here) → alive;
 * - this pid: alive when the start time matches this process (another logger or worker thread here), stale when it
 *   differs (a previous container incarnation that reused the pid);
 * - another pid: `process.kill(pid, 0)` decides (a pid reused by an unrelated process counts as alive).
 */
export function isStale(info: LockInfo | null): boolean {
  if (!info || typeof info.pid !== "number" || typeof info.host !== "string") return false;
  if (info.host !== HOST) return false;
  if (info.pid === process.pid) {
    return typeof info.started === "number" && Math.abs(info.started - PROCESS_STARTED) > START_TOLERANCE_MS;
  }
  return !processAlive(info.pid);
}

function readLock(file: string): { text: string; info: LockInfo | null } | null {
  let text: string;
  try {
    text = fs.readFileSync(file, "utf8");
  } catch (e) {
    if ((e as NodeJS.ErrnoException).code === "ENOENT") return null;
    throw e;
  }
  try {
    const info = JSON.parse(text) as LockInfo;
    return { text, info: typeof info === "object" && info !== null ? info : null };
  } catch {
    return { text, info: null };
  }
}

/** Removes a stale LOCK unless it changed since it was read. A guard file serializes concurrent takeovers. */
function removeStale(file: string, seen: string): void {
  const guard = file + ".takeover";
  let fd: number;
  try {
    fd = fs.openSync(guard, "wx");
  } catch (e) {
    if ((e as NodeJS.ErrnoException).code !== "EEXIST") throw e;
    try {
      if (Date.now() - fs.statSync(guard).mtimeMs > STEAL_GUARD_STALE_MS) fs.unlinkSync(guard);
    } catch {
      // another process removed it first
    }
    return;
  }
  fs.closeSync(fd);
  try {
    if (readLock(file)?.text === seen) fs.unlinkSync(file);
  } finally {
    fs.unlinkSync(guard);
  }
}

/** Claims `dir/LOCK` for this process. Returns the lock's content, or null when another live process holds it. */
function tryLock(dir: string): string | null {
  const file = path.join(dir, "LOCK");
  const content = JSON.stringify({ pid: process.pid, host: HOST, started: PROCESS_STARTED, token: randomBytes(8).toString("hex") });
  for (let attempt = 0; attempt < 3; attempt++) {
    try {
      const fd = fs.openSync(file, "wx");
      try {
        fs.writeSync(fd, content);
      } finally {
        fs.closeSync(fd);
      }
      return content;
    } catch (e) {
      if ((e as NodeJS.ErrnoException).code !== "EEXIST") throw e;
    }
    const held = readLock(file);
    if (held === null) continue; // released meanwhile
    if (!isStale(held.info)) return null;
    removeStale(file, held.text);
  }
  return null;
}

/** Releases a LOCK this process holds (only if it still holds exactly that content). */
export function releaseLock(dir: string, content: string): void {
  const file = path.join(dir, "LOCK");
  try {
    if (fs.readFileSync(file, "utf8") === content) fs.unlinkSync(file);
  } catch {
    // already gone
  }
}

/** A process folder held by this process. */
export interface ProcessFolder {
  dir: string;
  producerId: Buffer;
  nextSeq: number;
  lock: string;
}

/** Takes the first `p-N` whose LOCK is free or stale (continuing its sequence), else creates `p-<max+1>`. */
export function acquireProcessFolder(folder: string): ProcessFolder {
  for (let attempt = 0; attempt < 1000; attempt++) {
    const existing = processFolders(folder);
    for (const [, dir] of existing) {
      const lock = tryLock(dir);
      if (lock !== null) {
        try {
          return openProcessFolder(dir, lock);
        } catch (e) {
          releaseLock(dir, lock);
          throw e;
        }
      }
    }
    const n = existing.length ? existing[existing.length - 1]![0] + 1 : 1;
    try {
      fs.mkdirSync(path.join(folder, `p-${n}`));
    } catch (e) {
      if ((e as NodeJS.ErrnoException).code !== "EEXIST") throw e;
    }
  }
  throw new Error(`could not acquire a process folder in ${folder}`);
}

function openProcessFolder(dir: string, lock: string): ProcessFolder {
  const meta = path.join(dir, "process.json");
  let producerId: Buffer;
  try {
    const j = JSON.parse(fs.readFileSync(meta, "utf8")) as { producer_id?: unknown };
    if (typeof j.producer_id !== "string" || !/^[0-9a-fA-F]{32}$/.test(j.producer_id)) throw invalid(`${meta}: bad producer_id`);
    producerId = Buffer.from(j.producer_id, "hex");
  } catch (e) {
    if ((e as NodeJS.ErrnoException).code !== "ENOENT") throw e;
    producerId = randomId();
    atomicWriteSync(meta, JSON.stringify({ producer_id: producerId.toString("hex") }));
  }
  return { dir, producerId, nextSeq: recoverNextSeq(dir), lock };
}

const RECORD_PREFIX = Buffer.from('{"type":"record","seq":"');

/** `seq` of a record line written by any LatticeLog SDK (`{"type":"record","seq":"N",…`), else -1. */
export function recordSeq(buf: Buffer, start: number, end: number): number {
  const p = RECORD_PREFIX.length;
  if (end - start <= p || buf.compare(RECORD_PREFIX, 0, p, start, start + p) !== 0) return -1;
  let n = 0;
  for (let i = start + p; i < end; i++) {
    const c = buf[i]!;
    if (c === 0x22) return i > start + p ? n : -1;
    if (c < 0x30 || c > 0x39) return -1;
    n = n * 10 + (c - 0x30);
  }
  return -1;
}

/** Continues the dense sequence after the last complete record; a torn last line (crash mid-write) is cut off. */
export function recoverNextSeq(dir: string): number {
  const files = dataFiles(dir);
  const committed = committedThrough(dir);
  if (!files.length) return committed + 1;
  const [first, file] = files[files.length - 1]!;
  const fd = fs.openSync(file, "r+");
  try {
    const size = fs.fstatSync(fd).size;
    const block = 64 * 1024;
    // Find the end of the last complete line.
    let good = 0;
    for (let end = size; end > 0 && good === 0; ) {
      const start = Math.max(0, end - block);
      const buf = Buffer.allocUnsafe(end - start);
      fs.readSync(fd, buf, 0, buf.length, start);
      const nl = buf.lastIndexOf(0x0a);
      if (nl >= 0) good = start + nl + 1;
      end = start;
    }
    if (good !== size) {
      fs.ftruncateSync(fd, good);
      fs.fsyncSync(fd);
    }
    // The last record line, read backwards in blocks (a line may span blocks).
    let carry = Buffer.alloc(0);
    for (let end = good; end > 0; ) {
      const start = Math.max(0, end - block);
      const buf = Buffer.allocUnsafe(end - start);
      fs.readSync(fd, buf, 0, buf.length, start);
      const all = carry.length ? Buffer.concat([buf, carry]) : buf;
      // Lines fully inside `all`: each ends at a '\n'; the first one is complete only when start == 0.
      let lineEnd = all.length;
      if (all[lineEnd - 1] === 0x0a) lineEnd--;
      for (;;) {
        const nl = all.lastIndexOf(0x0a, lineEnd - 1);
        if (nl < 0 && start > 0) break;
        const seq = recordSeq(all, nl + 1, lineEnd);
        if (seq >= 0) return Math.max(seq + 1, committed + 1);
        if (nl < 0) break;
        lineEnd = nl;
      }
      carry = Buffer.from(all.subarray(0, lineEnd));
      end = start;
    }
    return Math.max(first, committed + 1); // a file is named after the first sequence it was opened for
  } finally {
    fs.closeSync(fd);
  }
}

/** Server mode: what the server confirmed (0 when nothing was shipped yet). */
export function committedThrough(dir: string): number {
  try {
    const j = JSON.parse(fs.readFileSync(path.join(dir, "cursor.json"), "utf8")) as { committed_through?: unknown };
    return typeof j.committed_through === "number" ? j.committed_through : 0;
  } catch {
    return 0;
  }
}

/**
 * Deletes whole closed files, oldest first, by age and/or total size of the source folder. The newest file of each
 * process folder may still be written and is never deleted; in a shipped process folder (with a `cursor.json`) a file
 * whose records the server has not all confirmed is never deleted either.
 */
export async function applyRetention(folder: string, maxAgeMs: number | undefined, maxBytes: number | undefined): Promise<void> {
  if (maxAgeMs === undefined && maxBytes === undefined) return;
  const closed: Array<{ mtime: number; file: string; size: number }> = [];
  let total = 0;
  for (const [, dir] of processFolders(folder)) {
    let files: Array<[number, string]>;
    try {
      files = await dataFilesAsync(dir);
    } catch {
      continue;
    }
    let cursor: number | undefined;
    try {
      const j = JSON.parse(await fsp.readFile(path.join(dir, "cursor.json"), "utf8")) as { committed_through?: unknown };
      if (typeof j.committed_through === "number") cursor = j.committed_through;
    } catch {
      cursor = undefined;
    }
    for (let i = 0; i < files.length; i++) {
      let st: fs.Stats;
      try {
        st = await fsp.stat(files[i]![1]);
      } catch {
        continue;
      }
      total += st.size;
      if (i + 1 < files.length) {
        const lastSeq = files[i + 1]![0] - 1;
        if (cursor !== undefined && lastSeq > cursor) continue;
        closed.push({ mtime: st.mtimeMs, file: files[i]![1], size: st.size });
      }
    }
  }
  closed.sort((a, b) => a.mtime - b.mtime);
  const now = Date.now();
  for (const c of closed) {
    const tooOld = maxAgeMs !== undefined && now - c.mtime > maxAgeMs;
    const tooBig = maxBytes !== undefined && total > maxBytes;
    if (!tooOld && !tooBig) continue;
    try {
      await fsp.unlink(c.file);
      total -= c.size;
    } catch {
      // gone already, or in use (Windows): next time
    }
  }
}
