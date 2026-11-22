import { test } from "node:test";
import assert from "node:assert/strict";
import * as fs from "node:fs";
import * as path from "node:path";
import pino from "pino";
import winston from "winston";
import * as ll from "../dist/index.js";
import { destination } from "../dist/pino.js";
import { LatticeLogTransport } from "../dist/winston.js";
import { attr, records, sleep, tempDir } from "./helpers.mjs";

test("pino destination: the call's values stay typed, bindings and errors are mapped", async () => {
  const dir = tempDir("pino");
  const log = ll.open(dir, { service: "svc", minLevel: "trace" });
  const logger = pino({ level: "trace" }, destination(log));
  const child = logger.child({ module: "billing" });
  class PaymentError extends Error {}
  child.info({ order_id: 991, total: 19.99, ratio: ll.f64(1), when: new Date(Date.UTC(2026, 0, 2)), big: 2n ** 63n, raw: Buffer.from([0, 255]) }, "order %d placed", 991);
  child.error({ err: new PaymentError("declined", { cause: new TypeError("sku") }), event: "payment_failed" }, "payment failed");
  logger.error(new RangeError("bad amount"));
  logger.warn({ event: "Heartbeat failed for group x" }); // free text: the message, not an event name
  logger.trace({ trace_id: "4bf92f3577b34da6a3ce929d0e0e4736", span_id: "00f067aa0ba902b7" }, "traced");
  await log.close();

  const recs = records(path.join(dir, "p-1")).map((r) => r.rec);
  assert.equal(recs.length, 5);
  const [placed, failed, range, heartbeat, traced] = recs;
  assert.equal(placed.severity_number, 9);
  assert.equal(placed.body, "order 991 placed");
  assert.equal(attr(placed, "module"), "billing");
  assert.equal(attr(placed, "pid"), process.pid);
  assert.equal(attr(placed, "order_id"), 991);
  assert.deepEqual(attr(placed, "when"), { $ts: "2026-01-02T00:00:00.000000000Z" });
  assert.deepEqual(attr(placed, "big"), { $u64: "9223372036854775808" });
  assert.deepEqual(attr(placed, "raw"), { $bytes: "AP8=" });
  const raw = fs.readFileSync(records(path.join(dir, "p-1"))[0].file, "utf8");
  assert.match(raw, /\["ratio",1\.0\]/, "a double stays a double");
  assert.equal(failed.severity_number, 17);
  assert.equal(failed.event, "payment_failed");
  assert.equal(failed.exception.type, "PaymentError");
  assert.equal(failed.exception.inner[0].type, "TypeError");
  assert.equal(attr(failed, "err"), undefined, "the error is the exception, not an attribute");
  assert.equal(range.exception.type, "RangeError");
  assert.equal(range.body, "bad amount");
  assert.equal(heartbeat.event, undefined);
  assert.equal(heartbeat.body, "Heartbeat failed for group x");
  assert.equal(traced.severity_number, 1);
  assert.equal(traced.trace_id, "4bf92f3577b34da6a3ce929d0e0e4736");
  assert.equal(traced.span_id, "00f067aa0ba902b7");
});

test("pino transport: a worker thread writes the folder from pino's lines", async () => {
  const dir = tempDir("pino-transport");
  const transport = pino.transport({
    target: new URL("../dist/pino-transport.js", import.meta.url).href,
    options: { folder: dir, service: "svc-worker", environment: "test" },
  });
  const logger = pino({ level: "info" }, transport);
  for (let i = 0; i < 100; i++) logger.info({ i, event: "row_imported" }, "row");
  logger.error({ err: new TypeError("broken") }, "failed");
  await new Promise((resolve) => {
    transport.on("close", resolve);
    transport.end();
  });
  const recs = records(path.join(dir, "p-1")).map((r) => r.rec);
  assert.equal(recs.length, 101);
  assert.deepEqual(
    recs.slice(0, 100).map((r) => attr(r, "i")),
    Array.from({ length: 100 }, (_, i) => i),
  );
  assert.equal(recs[0].event, "row_imported");
  assert.equal(recs[100].exception.type, "TypeError");
  assert.equal(recs[100].exception.message, "broken");
  assert.equal(JSON.parse(fs.readFileSync(path.join(dir, "latticelog.source.json"), "utf8")).service, "svc-worker");
});

test("winston transport: metadata keeps its types, errors become exceptions", async () => {
  const dir = tempDir("winston");
  const log = ll.open(dir, { service: "svc", minLevel: "trace" });
  const logger = winston.createLogger({
    level: "silly",
    format: winston.format.combine(winston.format.timestamp(), winston.format.errors({ stack: true })),
    defaultMeta: { component: "api" },
    transports: [new LatticeLogTransport({ logger: log })],
  });
  logger.info("order placed", { order_id: 991, total: 19.5, at: new Date(Date.UTC(2026, 0, 2)), event: "order_placed" });
  logger.error("payment failed", { err: new SyntaxError("bad json") });
  logger.warn("heads up");
  logger.debug("details", { n: 2n ** 63n });
  logger.log("silly", "very verbose");
  await sleep(10);
  await log.close();
  const recs = records(path.join(dir, "p-1")).map((r) => r.rec);
  assert.equal(recs.length, 5);
  const [placed, failed, warn, debug, silly] = recs;
  assert.equal(placed.body, "order placed");
  assert.equal(placed.event, "order_placed");
  assert.equal(attr(placed, "order_id"), 991);
  assert.equal(attr(placed, "total"), 19.5);
  assert.equal(attr(placed, "component"), "api");
  assert.deepEqual(attr(placed, "at"), { $ts: "2026-01-02T00:00:00.000000000Z" });
  assert.equal(attr(placed, "timestamp"), undefined, "the format's timestamp is the record time");
  assert.equal(failed.severity_number, 17);
  assert.equal(failed.exception.type, "SyntaxError");
  assert.equal(warn.severity_number, 13);
  assert.equal(debug.severity_number, 5);
  assert.deepEqual(attr(debug, "n"), { $u64: "9223372036854775808" });
  assert.equal(silly.severity_number, 1);
});
