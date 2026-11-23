import { test } from "node:test";
import assert from "node:assert/strict";
import * as fs from "node:fs";
import * as http from "node:http";
import * as path from "node:path";
import * as zlib from "node:zlib";
import * as ll from "../dist/index.js";
import { crc32 } from "../dist/ship.js";
import { files, range, records, sleep, tempDir } from "./helpers.mjs";

/**
 * A fake latticelog-server: validates every envelope (magic, CRC-32, gzip, dense records matching the header,
 * contexts defined before use) and keeps `committed_through` per producer like the real one. `script(n, req)` may
 * answer request n differently ({ status, body, headers }).
 */
async function fakeServer(script = () => undefined) {
  const state = { requests: [], committed: new Map(), stored: [] };
  const server = http.createServer((req, res) => {
    const chunks = [];
    req.on("data", (c) => chunks.push(c));
    req.on("end", () => {
      const buf = Buffer.concat(chunks);
      const n = state.requests.length;
      const r = {
        auth: req.headers.authorization,
        ns: req.headers["latticelog-namespace"],
        ack: req.headers["latticelog-ack"],
        type: req.headers["content-type"],
        url: req.url,
      };
      assert.equal(buf.subarray(0, 4).toString("latin1"), "LLIB");
      assert.deepEqual([buf[4], buf[5], buf[6]], [1, 1, 1]);
      r.gap = buf[7] === 1;
      r.producer = buf.subarray(8, 24).toString("hex");
      r.first = Number(buf.readBigUInt64LE(24));
      r.last = Number(buf.readBigUInt64LE(32));
      r.count = buf.readUInt32LE(40);
      assert.equal(buf.readUInt32LE(44), crc32(buf.subarray(48)), "CRC-32 of the body as sent");
      const body = zlib.gunzipSync(buf.subarray(48)).toString("utf8");
      const ls = body.split("\n").filter(Boolean).map((l) => JSON.parse(l));
      assert.equal(ls[0].type, "header");
      assert.equal(ls[0].store_id, r.producer);
      const defined = new Set();
      r.records = [];
      for (const l of ls.slice(1)) {
        if (l.type === "context") {
          if (l.parent !== undefined) assert.ok(defined.has(l.parent));
          defined.add(l.ref);
        } else {
          assert.ok(defined.has(l.context), `context ${l.context} defined in the batch`);
          r.records.push(l);
        }
      }
      assert.equal(r.records.length, r.count);
      assert.deepEqual(
        r.records.map((x) => Number(x.seq)),
        range(r.first, r.last),
      );
      state.requests.push(r);
      const answer = script(n, r);
      if (answer) {
        res.writeHead(answer.status, { "Content-Type": "application/json", ...(answer.headers ?? {}) });
        res.end(JSON.stringify(answer.body ?? {}));
        return;
      }
      const c = state.committed.get(r.producer) ?? 0;
      if (r.first > c + 1 && !r.gap) {
        res.writeHead(409, { "Content-Type": "application/json" });
        res.end(JSON.stringify({ error: "sequence_gap", committed_through: String(c) }));
        return;
      }
      for (const x of r.records) if (Number(x.seq) > c) state.stored.push(x);
      const now = Math.max(c, r.last);
      state.committed.set(r.producer, now);
      res.writeHead(200, { "Content-Type": "application/json" });
      res.end(JSON.stringify({ status: "stored", committed_through: String(now), ack: "FsyncConfirmed" }));
    });
  });
  await new Promise((r) => server.listen(0, "127.0.0.1", r));
  state.url = `http://127.0.0.1:${server.address().port}`;
  state.close = () => new Promise((r) => server.close(r));
  return state;
}

const fast = { pollIntervalMs: 20, retryBaseMs: 10, retryMaxMs: 50 };

test("ships every record once with its contexts, then deletes confirmed closed files", async () => {
  const srv = await fakeServer();
  const dir = tempDir("ship");
  const log = ll.open(dir, { service: "svc", fileMaxBytes: 8192, server: { endpoint: srv.url + "/", token: "t0k", namespace: "acme/prod", ...fast } });
  await log.operation("batch", { id: "op-9" }).run(async () => {
    for (let i = 0; i < 300; i++) {
      log.info("row {i}", { i, f: 0.5 });
      if (i % 50 === 0) await sleep(1);
    }
  });
  assert.ok(await log.flushShipped(10_000));
  const s = log.stats();
  assert.equal(s.committedThrough, 300);
  assert.ok(s.batchesShipped >= 1);
  await log.close();
  await srv.close();
  assert.deepEqual(
    srv.stored.map((r) => Number(r.seq)),
    range(1, 300),
  );
  assert.ok(srv.stored.every((r) => r.operation_id === "op-9" && r.attributes[1][1] === 0.5));
  const r0 = srv.requests[0];
  assert.equal(r0.auth, "Bearer t0k");
  assert.equal(r0.ns, "acme/prod");
  assert.equal(r0.ack, "fsync");
  assert.equal(r0.type, "application/vnd.latticelog.batch");
  assert.equal(r0.url, "/v1/ingest");
  assert.equal(r0.producer, log.producerId);
  const pdir = path.join(dir, "p-1");
  assert.equal(fs.readFileSync(path.join(pdir, "cursor.json"), "utf8"), '{"committed_through":300}');
  assert.equal(files(pdir).length, 1, "closed confirmed files are deleted, the newest stays");
});

test("retries after 503 (honouring Retry-After) and halves the batch after 413", async () => {
  const srv = await fakeServer((n, r) => {
    if (n === 0) return { status: 503, body: { error: "overloaded" }, headers: { "Retry-After": "0.05" } };
    if (r.count > 10) return { status: 413, body: { error: "too_large" } };
    return undefined;
  });
  const dir = tempDir("retry");
  const log = ll.open(dir, { service: "svc", server: { endpoint: srv.url, token: "t", ...fast } });
  for (let i = 0; i < 40; i++) log.info("x", { i });
  assert.ok(await log.flushShipped(10_000), JSON.stringify(log.stats()));
  const s = log.stats();
  assert.ok(s.shipRetries >= 1);
  await log.close();
  await srv.close();
  assert.deepEqual(
    srv.stored.map((r) => Number(r.seq)),
    range(1, 40),
  );
  assert.ok(srv.requests.some((r) => r.count <= 10 && r.count > 0), "smaller batches after 413");
});

test("a sequence_gap answer resends from the server's cursor", async () => {
  // The server forgets everything after the first request (as after losing its disk): 409 + its cursor, then resend.
  let forgot = false;
  const srv = await fakeServer((n, r) => {
    if (n === 1 && !forgot) {
      forgot = true;
      srv.committed.set(r.producer, 0);
      srv.stored.length = 0;
      return { status: 409, body: { error: "sequence_gap", committed_through: "0" } };
    }
    return undefined;
  });
  const dir = tempDir("gap");
  const log = ll.open(dir, { service: "svc", server: { endpoint: srv.url, token: "t", batchRecords: 10, keepAcknowledged: true, ...fast } });
  for (let i = 0; i < 30; i++) log.info("x", { i });
  assert.ok(await log.flushShipped(10_000), JSON.stringify(log.stats()));
  await log.close();
  await srv.close();
  assert.deepEqual(
    srv.stored.map((r) => Number(r.seq)),
    range(1, 30),
  );
});

test("an authentication failure stops shipping and keeps the records", async () => {
  const srv = await fakeServer(() => ({ status: 401, body: { error: "unauthorized", message: "bad token" } }));
  const dir = tempDir("auth");
  const log = ll.open(dir, { service: "svc", server: { endpoint: srv.url, token: "wrong", ...fast } });
  for (let i = 0; i < 5; i++) log.info("x", { i });
  assert.equal(await log.flushShipped(5_000), false);
  const s = log.stats();
  assert.equal(s.shipFailed, true);
  assert.match(s.shipError, /401 unauthorized: bad token/);
  await log.close();
  await srv.close();
  assert.deepEqual(seqs_(dir), range(1, 5));
  assert.equal(fs.readFileSync(path.join(dir, "p-1", "cursor.json"), "utf8"), '{"committed_through":0}', "nothing confirmed");
});

function seqs_(dir) {
  return records(path.join(dir, "p-1")).map((r) => Number(r.rec.seq));
}

test("without a server, records wait in the folder and ship at the next start", async () => {
  const dir = tempDir("offline");
  const down = ll.open(dir, { service: "svc", server: { endpoint: "http://127.0.0.1:9", token: "t", timeoutMs: 500, ...fast }, closeTimeoutMs: 200 });
  for (let i = 0; i < 20; i++) down.info("x", { i });
  assert.equal(await down.flushShipped(300), false);
  assert.ok(down.stats().shipRetries >= 1);
  assert.match(down.stats().shipError, /network/);
  await down.close();
  const srv = await fakeServer();
  const up = ll.open(dir, { service: "svc", server: { endpoint: srv.url, token: "t", ...fast } });
  up.info("later");
  assert.ok(await up.flushShipped(10_000));
  await up.close();
  await srv.close();
  assert.deepEqual(
    srv.stored.map((r) => Number(r.seq)),
    range(1, 21),
  );
});

test("the unconfirmed backlog is bounded: new records are refused and counted", async () => {
  const dir = tempDir("spool");
  const log = ll.open(dir, {
    service: "svc",
    fileMaxBytes: 4096,
    server: { endpoint: "http://127.0.0.1:9", token: "t", maxSpoolBytes: 16 * 1024, timeoutMs: 200, ...fast },
    closeTimeoutMs: 100,
  });
  for (let round = 0; round < 30; round++) {
    for (let i = 0; i < 50; i++) log.info("padding padding padding padding", { round, i });
    await log.flush();
    await sleep(60); // the shipper checks the backlog twice a second
  }
  await log.close();
  const s = log.stats();
  assert.ok(s.droppedSpoolFull > 0, JSON.stringify(s));
  assert.equal(s.written + s.droppedSpoolFull, 1500);
});

test("retention never deletes a closed file the server has not confirmed", async () => {
  const dir = tempDir("ship-retention");
  const log = ll.open(dir, {
    service: "svc",
    fileMaxBytes: 4096,
    maxBytes: 4096,
    server: { endpoint: "http://127.0.0.1:9", token: "t", timeoutMs: 200, ...fast },
    closeTimeoutMs: 100,
  });
  for (let i = 0; i < 300; i++) {
    log.info("padding padding padding padding", { i });
    if (i % 30 === 0) await log.flush();
  }
  await log.close();
  assert.equal(fs.readFileSync(path.join(dir, "p-1", "cursor.json"), "utf8"), '{"committed_through":0}');
  assert.deepEqual(seqs_(dir), range(1, 300), "every unshipped record is still there");
});
