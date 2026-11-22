// Producer cost and throughput of the TypeScript SDK against pino writing JSON to a file, on the same machine.
//
//   node bench/bench.mjs <folder> [records=500000]          every scenario, each in its own child process
//   node bench/bench.mjs <folder> <records> <scenario>      one scenario (used by the parent)
//
// Every scenario writes the same logical record (message + 3 attributes: int, float, string) and yields to the event
// loop every 1,000 records (`await setImmediate`), as a server between requests does: asynchronous writers (both the
// SDK and pino's sonic-boom in async mode) can only write while the loop turns. `call` is the time spent inside the
// logging calls; `end to end` runs until everything is handed to the OS (no fsync waited for). Results: one JSON
// line per scenario on stdout.

import { spawnSync } from "node:child_process";
import * as fs from "node:fs";
import * as path from "node:path";
import { fileURLToPath } from "node:url";

const [folder, recordsArg = "500000", scenario] = process.argv.slice(2);
if (!folder) {
  console.error("usage: node bench/bench.mjs <folder> [records] [scenario]");
  process.exit(2);
}
const N = Number(recordsArg);
const YIELD_EVERY = 1000;
const SCENARIOS = [
  "latticelog-balanced",
  "latticelog-fast",
  "latticelog-durable",
  "latticelog-paced",
  "pino-paced",
  "latticelog-burst",
  "latticelog-burst-block",
  "pino-async",
  "pino-sync",
  "pino-burst",
  "pino-transport",
  "pino-to-latticelog",
  "latency-latticelog",
  "latency-pino",
  "disabled-latticelog",
  "disabled-pino",
];

const tick = () => new Promise((r) => setImmediate(r));

function dirSize(p) {
  let total = 0;
  for (const e of fs.readdirSync(p, { withFileTypes: true })) {
    const f = path.join(p, e.name);
    total += e.isDirectory() ? dirSize(f) : e.name.endsWith(".jsonl") || e.name.endsWith(".log") ? fs.statSync(f).size : 0;
  }
  return total;
}

const PACED_RATE = 100_000; // records per second

/**
 * Runs `emit(i)` n times, yielding every YIELD_EVERY records (unless burst); with `paced`, the batches follow a
 * schedule of PACED_RATE records/s (a late batch runs at once, so the average holds). Returns ns spent in the calls.
 */
async function drive(emit, n, burst = false, paced = false) {
  let callNs = 0n;
  const start = performance.now();
  for (let i = 0; i < n; i += YIELD_EVERY) {
    const end = Math.min(n, i + YIELD_EVERY);
    const t0 = process.hrtime.bigint();
    for (let j = i; j < end; j++) emit(j);
    callNs += process.hrtime.bigint() - t0;
    if (paced) {
      const due = start + (end * 1000) / PACED_RATE;
      const wait = due - performance.now();
      await (wait > 1 ? new Promise((r) => setTimeout(r, wait)) : tick());
    } else if (!burst) await tick();
  }
  return Number(callNs);
}

function percentiles(samples) {
  samples.sort((a, b) => a - b);
  const p = (q) => samples[Math.min(samples.length - 1, Math.floor(q * samples.length))];
  return { p50_ns: Math.round(p(0.5)), p99_ns: Math.round(p(0.99)), p999_ns: Math.round(p(0.999)), max_ns: Math.round(samples[samples.length - 1]) };
}

async function run(name, dir) {
  const ll = await import("../dist/index.js");
  const out = { scenario: name, records: N };
  const t0 = process.hrtime.bigint();
  const finish = (extra = {}) => {
    const total = Number(process.hrtime.bigint() - t0);
    Object.assign(out, extra);
    const done = out.written ?? out.records;
    out.end_to_end_records_per_s = Math.round((done * 1e9) / total);
    out.bytes_per_record = Math.round(dirSize(dir) / done);
    out.max_rss_mib = Math.round(process.resourceUsage().maxRSS / 1024);
    return out;
  };
  if (name.startsWith("latticelog-")) {
    const durability = name === "latticelog-fast" ? "fast" : name === "latticelog-durable" ? "durable" : "balanced";
    const burst = name.startsWith("latticelog-burst");
    const log = ll.open(dir, { service: "bench", durability, saturation: name === "latticelog-burst-block" ? "block" : "drop" });
    let maxQueued = 0;
    const sampler = setInterval(() => (maxQueued = Math.max(maxQueued, log.stats().queued)), 5);
    const callNs = await drive((i) => log.info("order placed", { order_id: i, total: 19.99, sku: "A-1" }), N, burst, name === "latticelog-paced");
    clearInterval(sampler);
    out.max_queued_records = maxQueued;
    const tf = performance.now();
    const r = await log.flush();
    out.final_flush_ms = +(performance.now() - tf).toFixed(1);
    const s = log.stats();
    out.call_ns_per_record = +(callNs / N).toFixed(1);
    finish({ written: s.written, dropped: s.droppedSaturation, flush_level: r.level });
    await log.close();
    return out;
  }
  if (name === "pino-async" || name === "pino-sync" || name === "pino-burst" || name === "pino-paced") {
    const pino = (await import("pino")).default;
    const dest = pino.destination({ dest: path.join(dir, "pino.log"), sync: name === "pino-sync", minLength: name === "pino-sync" ? 0 : 4096 });
    if (name !== "pino-sync") await new Promise((r) => dest.once("ready", r));
    const logger = pino({ level: "info" }, dest);
    const callNs = await drive((i) => logger.info({ order_id: i, total: 19.99, sku: "A-1" }, "order placed"), N, name === "pino-burst", name === "pino-paced");
    const tf = performance.now();
    await new Promise((r) => dest.flush(r));
    out.final_flush_ms = +(performance.now() - tf).toFixed(1);
    out.call_ns_per_record = +(callNs / N).toFixed(1);
    return finish();
  }
  if (name === "pino-transport") {
    const pino = (await import("pino")).default;
    const transport = pino.transport({ target: "pino/file", options: { destination: path.join(dir, "pino.log"), mkdir: true } });
    const logger = pino({ level: "info" }, transport);
    const callNs = await drive((i) => logger.info({ order_id: i, total: 19.99, sku: "A-1" }, "order placed"), N);
    // thread-stream unreferences its worker: a referenced timer keeps the process alive until the worker closed.
    const keepAlive = setInterval(() => undefined, 1000);
    await new Promise((r) => {
      transport.once("close", r);
      transport.end();
    });
    clearInterval(keepAlive);
    out.call_ns_per_record = +(callNs / N).toFixed(1);
    return finish();
  }
  if (name === "pino-to-latticelog") {
    const pino = (await import("pino")).default;
    const { destination } = await import("../dist/pino.js");
    const log = ll.open(dir, { service: "bench" });
    const logger = pino({ level: "info" }, destination(log));
    const callNs = await drive((i) => logger.info({ order_id: i, total: 19.99, sku: "A-1" }, "order placed"), N);
    await log.flush();
    out.call_ns_per_record = +(callNs / N).toFixed(1);
    finish({ written: log.stats().written, dropped: log.stats().droppedSaturation });
    await log.close();
    return out;
  }
  if (name.startsWith("latency-")) {
    // One call at a time, timed individually (includes ~30-60 ns of timer overhead), 200,000 calls.
    const n = Math.min(N, 200_000);
    out.records = n;
    const samples = new Float64Array(n);
    let emit;
    let done;
    if (name === "latency-latticelog") {
      const log = ll.open(dir, { service: "bench" });
      emit = (i) => log.info("order placed", { order_id: i, total: 19.99, sku: "A-1" });
      done = () => log.close();
    } else {
      const pino = (await import("pino")).default;
      const dest = pino.destination({ dest: path.join(dir, "pino.log"), sync: false, minLength: 4096 });
      await new Promise((r) => dest.once("ready", r));
      const logger = pino({ level: "info" }, dest);
      emit = (i) => logger.info({ order_id: i, total: 19.99, sku: "A-1" }, "order placed");
      done = () => new Promise((r) => dest.flush(r));
    }
    for (let i = 0; i < n; i++) {
      const a = process.hrtime.bigint();
      emit(i);
      samples[i] = Number(process.hrtime.bigint() - a);
      if (i % 100 === 99) await tick();
    }
    await done();
    return Object.assign(out, percentiles(Array.from(samples)));
  }
  if (name.startsWith("disabled-")) {
    const n = N * 20;
    let emit;
    if (name === "disabled-latticelog") {
      const log = ll.open(dir, { service: "bench", minLevel: "info", exitHook: false });
      emit = (i) => log.debug("order placed", { order_id: i, total: 19.99, sku: "A-1" });
    } else {
      const pino = (await import("pino")).default;
      const logger = pino({ level: "info" }, pino.destination({ dest: path.join(dir, "pino.log"), sync: true }));
      emit = (i) => logger.debug({ order_id: i, total: 19.99, sku: "A-1" }, "order placed");
    }
    const callNs = await drive(emit, n, true);
    return { scenario: name, records: n, call_ns_per_record: +(callNs / n).toFixed(2) };
  }
  throw new Error(`unknown scenario ${name}`);
}

if (scenario) {
  const dir = path.join(folder, `${scenario}-${process.pid}`);
  fs.rmSync(dir, { recursive: true, force: true });
  fs.mkdirSync(dir, { recursive: true });
  const result = await run(scenario, dir);
  console.log(JSON.stringify(result));
  fs.rmSync(dir, { recursive: true, force: true });
} else {
  const self = fileURLToPath(import.meta.url);
  console.error(`node ${process.version}, ${N} records per scenario, folder ${folder}`);
  for (const s of SCENARIOS) {
    const n = s === "latticelog-durable" ? Math.min(N, 50_000) : N;
    const r = spawnSync(process.execPath, [self, folder, String(n), s], { encoding: "utf8", stdio: ["ignore", "pipe", "inherit"] });
    process.stdout.write(r.stdout || `{"scenario":"${s}","error":"exit ${r.status}"}\n`);
  }
}
