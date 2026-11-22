import { test } from "node:test";
import assert from "node:assert/strict";
import { decimal, duration, f64, timestamp, u64, utf16, uuid } from "../dist/index.js";
import { encodePairs, encodeValue, formatTimestamp } from "../dist/values.js";
import { encodeException, errorType } from "../dist/exception.js";
import { crc32 } from "../dist/ship.js";

test("numbers: safe integers are int64, everything else float64 with a '.' or an exponent", () => {
  assert.equal(encodeValue(42), "42");
  assert.equal(encodeValue(-7), "-7");
  assert.equal(encodeValue(1.5), "1.5");
  assert.equal(encodeValue(-0), "-0.0");
  assert.equal(encodeValue(2 ** 53), "9007199254740992.0"); // beyond the safe range: a double, not a guess
  assert.equal(encodeValue(1e21), "1e+21");
  assert.equal(encodeValue(1.5e-7), "1.5e-7");
  assert.equal(encodeValue(f64(1)), "1.0");
  assert.equal(encodeValue(NaN), '{"$f64bits":"7FF8000000000000"}');
  assert.equal(encodeValue(Infinity), '{"$f64":"Infinity"}');
  assert.equal(encodeValue(-Infinity), '{"$f64":"-Infinity"}');
});

test("bigints: int64, then UInt64, then decimal", () => {
  assert.equal(encodeValue(5n), "5");
  assert.equal(encodeValue(-(2n ** 63n)), "-9223372036854775808");
  assert.equal(encodeValue(2n ** 63n), '{"$u64":"9223372036854775808"}');
  assert.equal(encodeValue(2n ** 64n), '{"$dec":"18446744073709551616"}');
  assert.equal(encodeValue(-(2n ** 63n) - 1n), '{"$dec":"-9223372036854775809"}');
  assert.equal(encodeValue(u64(2n ** 64n - 1n)), '{"$u64":"18446744073709551615"}');
  assert.equal(encodeValue(u64(3)), "3");
  assert.throws(() => u64(-1));
});

test("strings: valid ones as JSON, lone surrogates as UTF-16LE bytes", () => {
  assert.equal(encodeValue('a"b\\c\n\u0001'), JSON.stringify('a"b\\c\n\u0001'));
  assert.equal(encodeValue("héllo 世界 🎉"), '"héllo 世界 🎉"');
  assert.equal(encodeValue("a\uD800b"), '{"$utf16le":"YQAA2GIA"}');
  assert.equal(encodeValue(utf16([0x61, 0xdc00])), '{"$utf16le":"YQAA3A=="}');
  // Keys cannot carry lone surrogates: replaced by U+FFFD (as in the Python SDK).
  assert.equal(encodePairs({ "k\uD800": 1 }), '["k�",1]');
});

test("bytes, dates, maps, sets, nested values and wrappers", () => {
  assert.equal(encodeValue(new Uint8Array([0, 255])), '{"$bytes":"AP8="}');
  assert.equal(encodeValue(Buffer.from("hi")), '{"$bytes":"aGk="}');
  assert.equal(encodeValue(new Uint8Array([1, 2, 3, 4]).subarray(1, 3)), '{"$bytes":"AgM="}');
  assert.equal(encodeValue(new Date(Date.UTC(2026, 0, 2, 3, 4, 5, 123))), '{"$ts":"2026-01-02T03:04:05.123000000Z"}');
  assert.equal(encodeValue(timestamp(1_767_323_045_123_456_789n)), '{"$ts":"2026-01-02T03:04:05.123456789Z"}');
  assert.equal(formatTimestamp(-1n), "1969-12-31T23:59:59.999999999Z");
  assert.equal(encodeValue(new Map([["a", 1], [2, "x"]])), '{"$map":[["a",1],["2","x"]]}');
  assert.equal(encodeValue({ b: 1, a: [true, null, undefined] }), '{"$map":[["b",1],["a",[true,null,null]]]}');
  assert.equal(encodeValue(new Set([1, 2])), "[1,2]");
  assert.equal(encodeValue(new Float64Array([1, 0.5])), "[1,0.5]");
  assert.equal(encodeValue(uuid("00000000-0000-0000-0000-0000000000FF")), '{"$uuid":"00000000-0000-0000-0000-0000000000ff"}');
  assert.throws(() => uuid("nope"));
  assert.equal(encodeValue(decimal("19.990")), '{"$dec":"19.990"}');
  assert.throws(() => decimal("1e5"));
  assert.equal(encodeValue(duration(1500)), '{"$dur_ns":"1500000000"}');
  assert.equal(encodeValue(duration(7n)), '{"$dur_ns":"7"}');
  // Cycles end at the nesting limit instead of overflowing the stack.
  const o = { name: "loop" };
  o.self = o;
  assert.match(encodeValue(o), /\[depth limit\]/);
});

test("errors keep their real type, message, stack and cause chain", () => {
  class PaymentError extends Error {}
  class Named extends Error {
    name = "ValidationError";
  }
  const plain = new Error("x");
  plain.name = "CustomName";
  assert.equal(errorType(new PaymentError("p")), "PaymentError");
  assert.equal(errorType(new Named("v")), "ValidationError");
  assert.equal(errorType(plain), "CustomName");
  assert.equal(errorType(new TypeError("t")), "TypeError");
  assert.equal(errorType("boom"), "string");

  const e = JSON.parse(encodeException(new PaymentError("declined", { cause: new TypeError("sku", { cause: "root" }) })));
  assert.equal(e.type, "PaymentError");
  assert.equal(e.message, "declined");
  assert.match(e.stack, /declined/);
  assert.equal(e.inner[0].type, "TypeError");
  assert.deepEqual(e.inner[0].inner[0], { type: "string", message: "root" });

  const agg = JSON.parse(encodeException(new AggregateError([new RangeError("a"), new SyntaxError("b")], "both")));
  assert.equal(agg.type, "AggregateError");
  assert.deepEqual(
    agg.inner.map((x) => x.type),
    ["RangeError", "SyntaxError"],
  );
  // A cause cycle stops.
  const a = new Error("a");
  const b = new Error("b", { cause: a });
  a.cause = b;
  assert.equal(JSON.parse(encodeException(a)).inner[0].inner, undefined);
});

test("CRC-32 matches the IEEE polynomial", () => {
  assert.equal(crc32(Buffer.from("123456789")), 0xcbf43926);
  assert.equal(crc32(Buffer.alloc(0)), 0);
});
