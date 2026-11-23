// Driven by tests/LatticeLog.Tests (SdkTests.cs, SourcesTests.cs). Needs the compiled package (`npm run build`).
//
//   node smoke.mjs offline <folder>                      write 300 records, no server
//   node smoke.mjs ship <folder> <endpoint> <token>      write 200 more (record 300 has every value type), ship all
//   node smoke.mjs local <folder>                        two concurrent writers (p-1, p-2) of service ts-svc, with errors

import * as ll from "../dist/index.js";

class KeyError extends Error {}
class PaymentError extends Error {}
class QuotaExceeded extends Error {
  constructor(tenant) {
    super(`tenant ${tenant} over quota`);
  }
}

const [mode, folder, endpoint, token] = process.argv.slice(2);

if (mode === "offline") {
  const log = ll.open(folder, { service: "ts-orders", durability: "durable" });
  for (let i = 0; i < 300; i++) log.info("order {i}", { i });
  await log.close();
} else if (mode === "ship") {
  const log = ll.open(folder, {
    service: "ts-orders",
    server: { endpoint, token, namespace: "acme/prod", pollIntervalMs: 20 },
  });
  for (let i = 300; i < 500; i++) {
    if (i === 300) {
      log.operation("checkout", { id: "op-300", attributes: { cart: 7 } }).run(() => {
        log.error(
          "special {i}",
          {
            i,
            ratio: ll.f64(1), // a JavaScript 1.0 is the number 1: f64() keeps it a double
            big: 2n ** 63n,
            nan: NaN,
            raw: new Uint8Array([0x00, 0xff]),
            when: ll.timestamp(1_767_323_045_123_456_000n),
            id: ll.uuid("00000000-0000-0000-0000-0000000000ff"),
            price: ll.decimal("19.990"),
            nested: new Map([["a", [1, 2.5, "x"]]]),
            odd: "a\uD800b",
            unicode: "héllo 世界 🎉",
          },
          {
            traceId: "4bf92f3577b34da6a3ce929d0e0e4736",
            error: new PaymentError("payment failed", { cause: new KeyError("sku") }),
          },
        );
      });
    } else {
      log.info("order {i}", { i });
    }
  }
  const ok = await log.flushShipped(30_000);
  console.log(ok ? "shipped" : `not shipped: ${JSON.stringify(log.stats())}`);
  await log.close();
  process.exitCode = ok ? 0 : 1;
} else if (mode === "local") {
  const first = ll.open(folder, { service: "ts-svc", environment: "test", durability: "durable" });
  const second = ll.open(folder, { service: "ts-svc", environment: "test" }); // its own p-2
  for (const [target, n] of [
    [first, 30],
    [second, 10],
  ]) {
    for (let i = 0; i < n; i++) target.info("request completed", { i }, { event: "request_completed" });
    if (target === first) {
      target.event("order_placed", { items: 7, total: 19.5 });
      let parse;
      try {
        JSON.parse("{oops");
      } catch (e) {
        parse = e;
      }
      target.error("parse failed", { source: "upload" }, { event: "parse_failed", error: parse });
      target.error("quota check", null, { event: "quota_check", error: new QuotaExceeded(9) });
    } else {
      target.error("quota check", null, { event: "quota_check", error: new QuotaExceeded(3) });
    }
  }
  await first.close();
  await second.close();
} else {
  console.error(`unknown mode ${mode}`);
  process.exitCode = 2;
}
