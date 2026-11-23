import { test } from "node:test";
import assert from "node:assert/strict";
import * as fs from "node:fs";
import * as path from "node:path";
import { spawn, spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import * as ll from "../dist/index.js";
import { attr, files, lines, range, records, seqs, sleep, tempDir } from "./helpers.mjs";

const here = path.dirname(fileURLToPath(import.meta.url));
const indexUrl = new URL("../dist/index.js", import.meta.url).href;

/** Runs a small ESM program in a child node process (the SDK imported as `ll`). */
function child(code, ...args) {
  const r = spawnSync(process.execPath, ["--input-type=module", "-e", `import * as ll from ${JSON.stringify(indexUrl)};\n${code}`, ...args], {
    encoding: "utf8",
  });
  return r;
}

test("writes official latticelog.jsonl with exact types and a manifest", async () => {
  const dir = tempDir("types");
  const log = ll.open(dir, { service: "orders-api", environment: "dev", resource: { "service.version": "1.4.2" } });
  assert.ok(log.info("order {id} placed", { id: 991, total: 19.99, ok: true, none: null, ratio: ll.f64(1), big: 2n ** 63n }));
  log.warn(null, { k: "v" }, { event: "stock_low", correlationId: "c-1", traceId: "4BF92F3577B34DA6A3CE929D0E0E4736", spanId: "00f067aa0ba902b7" });
  const r = await log.flush({ fsync: true });
  assert.equal(r.level, "FsyncConfirmed");
  assert.equal(r.lastSeq, 2);
  await log.close();

  const m = JSON.parse(fs.readFileSync(path.join(dir, "latticelog.source.json"), "utf8"));
  assert.equal(m.format, "latticelog.source");
  assert.equal(m.version, 1);
  assert.equal(m.service, "orders-api");
  assert.equal(m.environment, "dev");
  assert.equal(m.data_format, "latticelog.jsonl");
  assert.equal(m.layout, "process-folders");
  assert.equal(m.producer, "latticelog-typescript");
  assert.match(m.producer_id, /^[0-9a-f]{32}$/);
  assert.match(m.created, /^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$/);

  const pdir = path.join(dir, "p-1");
  const process_ = JSON.parse(fs.readFileSync(path.join(pdir, "process.json"), "utf8"));
  assert.deepEqual(files(pdir).map((f) => path.basename(f)), ["latticelog-00000000000000000001.jsonl"]);
  const header = lines(files(pdir)[0])[0];
  assert.equal(header.store_id, process_.producer_id);
  assert.equal(log.producerId, process_.producer_id);

  const recs = records(pdir);
  assert.deepEqual(seqs(recs), [1, 2]);
  const [a, b] = recs;
  assert.match(a.rec.time, /^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{9}Z$/);
  assert.equal(a.rec.severity_number, 9);
  assert.equal(a.rec.body, "order {id} placed");
  assert.deepEqual(a.chain[0].attributes, [
    ["service.name", "orders-api"],
    ["deployment.environment.name", "dev"],
    ["service.version", "1.4.2"],
  ]);
  assert.equal(a.chain[0].kind, "Resource");
  // Raw text: 1.0 stays a float on the wire, 991 an integer, the u64 a wrapper.
  const raw = fs.readFileSync(a.file, "utf8");
  assert.match(raw, /\["id",991\],\["total",19\.99\],\["ok",true\],\["none",null\],\["ratio",1\.0\],\["big",\{"\$u64":"9223372036854775808"\}\]/);
  assert.equal(b.rec.body, undefined);
  assert.equal(b.rec.event, "stock_low");
  assert.equal(b.rec.severity_number, 13);
  assert.equal(b.rec.correlation_id, "c-1");
  assert.equal(b.rec.trace_id, "4bf92f3577b34da6a3ce929d0e0e4736");
  assert.equal(b.rec.span_id, "00f067aa0ba902b7");
  assert.equal(a.rec.context, b.rec.context); // one context line for both
  assert.ok(!fs.existsSync(path.join(pdir, "LOCK")), "LOCK is removed at close");
});

test("sequences stay dense across restarts, and concurrent loggers get their own folder", async () => {
  const dir = tempDir("dense");
  const a = ll.open(dir, { service: "svc" });
  const b = ll.open(dir, { service: "svc" }); // p-1 is held: p-2
  assert.equal(path.basename(a.processDir), "p-1");
  assert.equal(path.basename(b.processDir), "p-2");
  assert.notEqual(a.producerId, b.producerId);
  for (let i = 0; i < 5; i++) a.info("a", { i });
  for (let i = 0; i < 3; i++) b.info("b", { i });
  await a.close();
  await b.close();
  const again = ll.open(dir, { service: "svc" }); // takes p-1 again and continues its sequence
  assert.equal(path.basename(again.processDir), "p-1");
  again.info("after restart");
  await again.close();
  assert.deepEqual(seqs(records(path.join(dir, "p-1"))), range(1, 6));
  assert.deepEqual(seqs(records(path.join(dir, "p-2"))), range(1, 3));
  assert.equal(files(path.join(dir, "p-1")).length, 2); // a new file at every start
});

test("a torn last line is cut off and the sequence continues", async () => {
  const dir = tempDir("torn");
  const log = ll.open(dir, { service: "svc" });
  for (let i = 0; i < 3; i++) log.info("x", { i });
  await log.close();
  const f = files(path.join(dir, "p-1"))[0];
  fs.appendFileSync(f, '{"type":"record","seq":"4","context":1,"time":"2026-'); // crash mid-write
  const again = ll.open(dir, { service: "svc" });
  again.info("next");
  await again.close();
  assert.ok(fs.readFileSync(f, "utf8").endsWith("}\n"));
  assert.deepEqual(seqs(records(path.join(dir, "p-1"))), [1, 2, 3, 4]);
});

test("a folder belongs to one service", () => {
  const dir = tempDir("owner");
  ll.open(dir, { service: "one", exitHook: false });
  assert.throws(() => ll.open(dir, { service: "two" }), /belongs to service "one"/);
  assert.throws(() => ll.open(dir, { service: "" }), /service is required/);
});

test("contexts are defined once per file and again after rotation", async () => {
  const dir = tempDir("rotate");
  const log = ll.open(dir, { service: "svc", fileMaxBytes: 4096 });
  const op = log.operation("import", { id: "op-1", attributes: { file: "a.csv" } });
  for (let i = 0; i < 200; i++) log.info("row {i} imported with some padding text", { i }, { context: op });
  await log.close();
  const pdir = path.join(dir, "p-1");
  const fs_ = files(pdir);
  assert.ok(fs_.length > 3, `${fs_.length} files`);
  for (const f of fs_) {
    const ls = lines(f);
    assert.ok(fs.statSync(f).size < 4096 + 600, `${f} is ${fs.statSync(f).size} bytes`);
    assert.equal(ls.filter((l) => l.type === "context").length, 2, "resource + operation, once per file");
    const firstSeq = Number(path.basename(f).slice(11, 31));
    assert.equal(Number(ls.find((l) => l.type === "record").seq), firstSeq, "named after its first sequence");
  }
  const recs = records(pdir);
  assert.deepEqual(seqs(recs), range(1, 200));
  assert.ok(recs.every((r) => r.rec.operation_id === "op-1" && r.chain[0].name === "import"));
});

test("contexts follow async code, nest, and can be passed explicitly", async () => {
  const dir = tempDir("ctx");
  const log = ll.open(dir, { service: "svc" });
  await log.operation("checkout", { id: "op-1" }).run(async () => {
    await sleep(2);
    log.info("in checkout");
    await log.operation("pay", { id: "op-2", attributes: { amount: 5 } }).run(async () => {
      await new Promise((r) => setTimeout(r, 1));
      log.info("in pay");
      await log.scope("card", { brand: "visa" }).run(async () => {
        await Promise.resolve();
        log.info("in card");
      });
    });
    setImmediate(() => log.info("from a callback"));
    await sleep(5);
  });
  log.info("outside");
  const explicit = log.scope("explicit");
  log.info("given", null, { context: explicit });
  assert.equal(ll.currentContext(), undefined);
  await log.close();

  const recs = records(path.join(dir, "p-1"));
  const by = Object.fromEntries(recs.map((r) => [r.rec.body, r]));
  assert.equal(by["in checkout"].rec.operation_id, "op-1");
  assert.equal(by["in checkout"].rec.parent_operation_id, undefined);
  assert.equal(by["in pay"].rec.operation_id, "op-2");
  assert.equal(by["in pay"].rec.parent_operation_id, "op-1");
  assert.deepEqual(by["in pay"].chain[0].attributes, [
    ["operation.name", "pay"],
    ["amount", 5],
  ]);
  assert.equal(by["in card"].rec.operation_id, "op-2"); // a scope inherits the operation
  assert.deepEqual(
    by["in card"].chain.map((c) => c.kind),
    ["Scope", "Operation", "Operation", "Resource"],
  );
  assert.equal(by["from a callback"].rec.operation_id, "op-1");
  assert.equal(by["outside"].rec.operation_id, undefined);
  assert.deepEqual(by["given"].chain.map((c) => c.name ?? c.kind), ["explicit", "Resource"]);
  // A random operation id when none is given.
  assert.match(log.operation("x").operationId, /^[0-9a-f]{16}$/);
});

test("many concurrent async tasks get unique dense sequences", async () => {
  const dir = tempDir("tasks");
  const log = ll.open(dir, { service: "svc" });
  await Promise.all(
    range(0, 7).map(async (t) => {
      for (let i = 0; i < 2000; i++) {
        log.info("t", { t, i });
        if (i % 100 === 0) await sleep(0);
      }
    }),
  );
  await log.close();
  const recs = records(path.join(dir, "p-1"));
  assert.deepEqual(seqs(recs), range(1, 16000));
  for (let t = 0; t < 8; t++) {
    assert.deepEqual(
      recs.filter((r) => attr(r.rec, "t") === t).map((r) => attr(r.rec, "i")),
      range(0, 1999),
    );
  }
  assert.equal(log.stats().written, 16000);
});

test("several processes share a folder, each in its own p-N", async () => {
  const dir = tempDir("procs");
  const code = `const log = ll.open(process.argv[1], { service: "svc" });
for (let i = 0; i < 500; i++) { log.info("p", { i }); if (i % 50 === 0) await new Promise((r) => setTimeout(r, 1)); }
await log.close();`;
  const procs = range(1, 4).map(
    () =>
      new Promise((resolve) => {
        const c = spawn(process.execPath, ["--input-type=module", "-e", `import * as ll from ${JSON.stringify(indexUrl)};\n${code}`, dir], {
          stdio: "inherit",
        });
        c.on("exit", resolve);
      }),
  );
  assert.deepEqual(await Promise.all(procs), [0, 0, 0, 0]);
  const dirs = fs.readdirSync(dir).filter((d) => d.startsWith("p-"));
  assert.ok(dirs.length >= 1 && dirs.length <= 4, dirs.join());
  let total = 0;
  for (const d of dirs) {
    const s = seqs(records(path.join(dir, d)));
    assert.deepEqual(s, range(1, s.length), `${d} is dense`);
    total += s.length;
  }
  assert.equal(total, 2000);
});

test("a LOCK left by a dead process is taken over; a live one is respected", async () => {
  const dir = tempDir("stale");
  const first = ll.open(dir, { service: "svc" });
  first.info("one");
  await first.close();
  const pdir = path.join(dir, "p-1");
  const host = (await import("node:os")).hostname();
  // A process that has exited: its pid is free.
  const dead = spawnSync(process.execPath, ["-e", "process.stdout.write(String(process.pid))"], { encoding: "utf8" });
  fs.writeFileSync(path.join(pdir, "LOCK"), JSON.stringify({ pid: Number(dead.stdout), host, started: 1, token: "x" }));
  const again = ll.open(dir, { service: "svc" });
  assert.equal(path.basename(again.processDir), "p-1");
  await again.close();
  // A live process (this test's parent) on this host: p-1 is not touched.
  fs.writeFileSync(path.join(pdir, "LOCK"), JSON.stringify({ pid: process.ppid, host, started: 1, token: "y" }));
  const other = ll.open(dir, { service: "svc" });
  assert.equal(path.basename(other.processDir), "p-2");
  await other.close();
  // Another host, or an empty LOCK (the Python/Rust SDKs hold an OS lock instead): never taken over.
  fs.writeFileSync(path.join(pdir, "LOCK"), JSON.stringify({ pid: 1, host: "elsewhere", started: 1, token: "z" }));
  fs.writeFileSync(path.join(dir, "p-2", "LOCK"), "");
  const third = ll.open(dir, { service: "svc" });
  assert.equal(path.basename(third.processDir), "p-3");
  await third.close();
  assert.deepEqual(seqs(records(pdir)), [1]); // nothing else written into p-1 after the takeover
});

test("levels filter, and a closed logger counts what it refuses", async () => {
  const dir = tempDir("levels");
  const log = ll.open(dir, { service: "svc", minLevel: "warn" });
  assert.equal(log.info("skipped"), false);
  assert.equal(log.isEnabled("info"), false);
  assert.equal(log.error("kept"), true);
  log.setMinLevel("debug");
  assert.equal(log.debug("now kept"), true);
  assert.equal(log.log(24, "fatal4"), true);
  await log.close();
  assert.equal(log.info("late"), false);
  const s = log.stats();
  assert.equal(s.droppedAfterClose, 1);
  assert.equal(s.written, 3);
  assert.deepEqual(
    records(path.join(dir, "p-1")).map((r) => r.rec.severity_number),
    [17, 5, 24],
  );
});

test("a full queue drops the newest records and counts them; 'block' writes them instead", async () => {
  const dir = tempDir("full");
  const drop = ll.open(dir, { service: "svc", queueChars: 64 * 1024 });
  let accepted = 0;
  for (let i = 0; i < 5000; i++) if (drop.info("x".repeat(100), { i })) accepted++;
  await drop.close();
  const s = drop.stats();
  assert.ok(s.droppedSaturation > 0);
  assert.equal(accepted + s.droppedSaturation, 5000);
  assert.equal(s.written, accepted);

  const dir2 = tempDir("block");
  const block = ll.open(dir2, { service: "svc", queueChars: 64 * 1024, saturation: "block" });
  for (let i = 0; i < 5000; i++) assert.ok(block.info("x".repeat(100), { i }));
  await block.close();
  assert.equal(block.stats().droppedSaturation, 0);
  assert.deepEqual(seqs(records(path.join(dir2, "p-1"))), range(1, 5000));
});

test("retention deletes closed files only, oldest first", async () => {
  const dir = tempDir("retention");
  const log = ll.open(dir, { service: "svc", fileMaxBytes: 4096, maxBytes: 12 * 1024 });
  for (let i = 0; i < 400; i++) {
    log.info("some text to fill the files quickly", { i });
    if (i % 20 === 0) await log.flush();
  }
  await log.close();
  const left = files(path.join(dir, "p-1"));
  const total = left.reduce((n, f) => n + fs.statSync(f).size, 0);
  assert.ok(total <= 12 * 1024 + 4096 + 600, `${total} bytes left`);
  const s = seqs(records(path.join(dir, "p-1")));
  assert.equal(s[s.length - 1], 400, "the newest records are kept");
  assert.deepEqual(s, range(s[0], 400), "what is left is a gap-free suffix");
});

test("errors keep their type and causes; durable mode confirms fsync; time can be set", async () => {
  const dir = tempDir("durable");
  const log = ll.open(dir, { service: "svc", durability: "durable" });
  class PaymentError extends Error {}
  log.error("failed", null, { error: new PaymentError("declined", { cause: new RangeError("amount") }), time: new Date(Date.UTC(2026, 0, 2)) });
  log.info("ns", null, { time: 1_767_323_045_123_456_789n });
  log.warn(new TypeError("an error as the message"));
  const r = await log.flush();
  assert.equal(r.level, "FsyncConfirmed"); // every batch is fsynced in durable mode
  await log.close();
  const [a, b, c] = records(path.join(dir, "p-1"));
  assert.equal(c.rec.body, "an error as the message");
  assert.equal(c.rec.exception.type, "TypeError");
  assert.equal(a.rec.exception.type, "PaymentError");
  assert.equal(a.rec.exception.inner[0].type, "RangeError");
  assert.equal(a.rec.time, "2026-01-02T00:00:00.000000000Z");
  assert.equal(b.rec.time, "2026-01-02T03:04:05.123456789Z");
});

test("records still queued are written when the process exits (process.exit, uncaught exception)", () => {
  const dir = tempDir("exit");
  const ok = child(`const log = ll.open(process.argv[1], { service: "svc", durability: "fast" });
for (let i = 0; i < 1000; i++) log.info("before exit", { i });
process.exit(0);`, path.join(dir, "a"));
  assert.equal(ok.status, 0, ok.stderr);
  assert.deepEqual(seqs(records(path.join(dir, "a", "p-1"))), range(1, 1000));
  assert.ok(!fs.existsSync(path.join(dir, "a", "p-1", "LOCK")));

  const crash = child(`const log = ll.open(process.argv[1], { service: "svc" });
setTimeout(() => { for (let i = 0; i < 10; i++) log.info("t", { i }); throw new Error("boom"); }, 5);`, path.join(dir, "b"));
  assert.notEqual(crash.status, 0);
  assert.match(crash.stderr, /boom/);
  assert.deepEqual(seqs(records(path.join(dir, "b", "p-1"))), range(1, 10));
});

test("the periodic balanced fsync and flush levels", async () => {
  const dir = tempDir("balanced");
  const log = ll.open(dir, { service: "svc" });
  log.info("a");
  const r1 = await log.flush();
  assert.equal(r1.level, "WrittenToOs");
  log.info("b");
  const r2 = await log.flush({ fsync: true });
  assert.equal(r2.level, "FsyncConfirmed");
  assert.equal(r2.lastSeq, 2);
  await log.close();
  const r3 = await log.flush();
  assert.equal(r3.lastSeq, 2);
});

test("await using closes the logger", async () => {
  const dir = tempDir("using");
  {
    const log = ll.open(dir, { service: "svc" });
    await log[Symbol.asyncDispose]();
  }
  assert.ok(!fs.existsSync(path.join(dir, "p-1", "LOCK")));
});

void here;
