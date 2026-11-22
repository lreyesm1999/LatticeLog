// Lossless typed values of latticelog.jsonl v1 (specs/jsonl-exchange.md). Every value keeps its identity on the wire:
// integers are int64, other numbers are float64 (always written with a "." or an exponent), NaN keeps its bits,
// bigints beyond int64 become UInt64 or Decimal, and the wrappers below carry UUIDs, decimals (with their scale),
// durations, nanosecond timestamps and forced doubles.

const INT64_MIN = -(2n ** 63n);
const INT64_MAX = 2n ** 63n - 1n;
const UINT64_MAX = 2n ** 64n - 1n;
/** Nesting limit of values (specs/event-model.md §3). Deeper values are written as the string "[depth limit]". */
const MAX_DEPTH = 32;

/** A value already encoded as JSON. Created by the wrapper functions; never built from untrusted text. */
export class TypedValue {
  /** @internal */
  readonly json: string;
  /** @internal */
  constructor(json: string) {
    this.json = json;
  }
  toJSON(): unknown {
    return JSON.parse(this.json);
  }
}

const UUID_RE = /^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$/;
const DECIMAL_RE = /^[+-]?(\d+(\.\d*)?|\.\d+)$/;

/** A UUID (`$uuid`). Accepts the 8-4-4-4-12 hex form; throws on anything else. */
export function uuid(text: string): TypedValue {
  if (!UUID_RE.test(text)) throw new TypeError(`not a UUID: ${JSON.stringify(text)}`);
  return new TypedValue(`{"$uuid":"${text.toLowerCase()}"}`);
}

/** A decimal number (`$dec`) written exactly as given, so its scale is kept: `decimal("19.990")` ≠ `decimal("19.99")`. */
export function decimal(text: string | bigint): TypedValue {
  const s = typeof text === "bigint" ? text.toString() : text;
  if (!DECIMAL_RE.test(s)) throw new TypeError(`not a decimal: ${JSON.stringify(s)}`);
  return new TypedValue(`{"$dec":"${s}"}`);
}

/** A duration (`$dur_ns`): nanoseconds as a bigint, or milliseconds as a number (`duration(1500)` = 1.5 s). */
export function duration(value: bigint | number): TypedValue {
  const ns = typeof value === "bigint" ? value : BigInt(Math.round(value * 1e6));
  return new TypedValue(`{"$dur_ns":"${ns}"}`);
}

/** A timestamp (`$ts`) with nanosecond precision (a `Date` only has milliseconds): Unix nanoseconds as a bigint. */
export function timestamp(unixNanos: bigint): TypedValue {
  return new TypedValue(`{"$ts":"${formatTimestamp(unixNanos)}"}`);
}

/** Forces float64 for a number JavaScript would write as an integer: `f64(1)` is stored as `1.0`, not Int64 `1`. */
export function f64(value: number): TypedValue {
  return new TypedValue(encodeFloat(value));
}

/** An unsigned 64-bit integer: Int64 when it fits, else UInt64 (`$u64`). */
export function u64(value: bigint | number): TypedValue {
  const v = BigInt(value);
  if (v < 0n || v > UINT64_MAX) throw new RangeError(`not a u64: ${v}`);
  return new TypedValue(v <= INT64_MAX ? v.toString() : `{"$u64":"${v}"}`);
}

/** Raw UTF-16 code units (`$utf16le` when they hold unpaired surrogates, else a plain string). */
export function utf16(units: Uint16Array | readonly number[]): TypedValue {
  return new TypedValue(encodeString(String.fromCharCode(...units)));
}

// ------------------------------------------------------------------------------------------------- encoding

const f64Buf = new Float64Array(1);
const u64View = new BigUint64Array(f64Buf.buffer);

/** float64: always with a "." or an exponent (`1.0`, `-0.0`, `1e+21`); NaN keeps its bits; infinities are named. */
export function encodeFloat(v: number): string {
  if (Number.isFinite(v)) {
    if (Object.is(v, -0)) return "-0.0";
    const s = String(v);
    for (let i = 0; i < s.length; i++) {
      const c = s.charCodeAt(i);
      if (c === 46 /* . */ || c === 101 /* e */) return s;
    }
    return s + ".0";
  }
  if (Number.isNaN(v)) {
    f64Buf[0] = v;
    return `{"$f64bits":"${u64View[0]!.toString(16).toUpperCase().padStart(16, "0")}"}`;
  }
  return v > 0 ? '{"$f64":"Infinity"}' : '{"$f64":"-Infinity"}';
}

/** A JSON number: int64 for safe integers (except -0), float64 otherwise. */
export function encodeNumber(v: number): string {
  return Number.isSafeInteger(v) && !Object.is(v, -0) ? String(v) : encodeFloat(v);
}

export function encodeBigInt(v: bigint): string {
  if (v >= INT64_MIN && v <= INT64_MAX) return v.toString();
  if (v > 0n && v <= UINT64_MAX) return `{"$u64":"${v}"}`;
  return `{"$dec":"${v}"}`;
}

/** A JSON string, or `$utf16le` when the string holds unpaired surrogates (JSON text cannot carry them as UTF-8). */
export function encodeString(s: string): string {
  if (s.isWellFormed()) return JSON.stringify(s);
  const b = Buffer.alloc(s.length * 2);
  for (let i = 0; i < s.length; i++) b.writeUInt16LE(s.charCodeAt(i), i * 2);
  return `{"$utf16le":"${b.toString("base64")}"}`;
}

/** Keys must be valid Unicode: unpaired surrogates become U+FFFD (as the Python SDK does). */
export function encodeKey(k: string): string {
  return JSON.stringify(k.isWellFormed() ? k : k.toWellFormed());
}

function pad(n: number, width: number): string {
  return String(n).padStart(width, "0");
}

/** RFC 3339 UTC with 9 fractional digits, from Unix nanoseconds. */
export function formatTimestamp(unixNanos: bigint): string {
  let secs = unixNanos / 1_000_000_000n;
  let frac = unixNanos % 1_000_000_000n;
  if (frac < 0n) {
    frac += 1_000_000_000n;
    secs -= 1n;
  }
  return secondPrefix(Number(secs)) + "." + pad(Number(frac), 9) + "Z";
}

/** `YYYY-MM-DDTHH:MM:SS` of a Unix second. */
export function secondPrefix(secs: number): string {
  const d = new Date(secs * 1000);
  return (
    pad(d.getUTCFullYear(), 4) +
    "-" +
    pad(d.getUTCMonth() + 1, 2) +
    "-" +
    pad(d.getUTCDate(), 2) +
    "T" +
    pad(d.getUTCHours(), 2) +
    ":" +
    pad(d.getUTCMinutes(), 2) +
    ":" +
    pad(d.getUTCSeconds(), 2)
  );
}

/** A `Date` (millisecond precision) as a JSON timestamp string; null when the date is invalid. */
export function dateTimestamp(d: Date): string | null {
  const ms = d.getTime();
  if (!Number.isFinite(ms)) return null;
  const secs = Math.floor(ms / 1000);
  return '"' + secondPrefix(secs) + "." + pad((ms - secs * 1000) * 1_000_000, 9) + 'Z"';
}

function isPlainObject(v: object): boolean {
  const p = Object.getPrototypeOf(v);
  return p === null || p === Object.prototype;
}

/** JavaScript value → its lossless JSON form (a string of JSON). */
export function encodeValue(v: unknown, depth = 0): string {
  switch (typeof v) {
    case "string":
      return encodeString(v);
    case "number":
      return encodeNumber(v);
    case "boolean":
      return v ? "true" : "false";
    case "bigint":
      return encodeBigInt(v);
    case "undefined":
      return "null";
    case "symbol":
      return encodeString(v.toString());
    case "function":
      return encodeString(`[function ${v.name || "anonymous"}]`);
  }
  if (v === null) return "null";
  if (depth >= MAX_DEPTH) return '"[depth limit]"';
  const o = v as object;
  if (o instanceof TypedValue) return o.json;
  if (o instanceof Date) return '{"$ts":' + (dateTimestamp(o) ?? '"invalid"') + "}";
  if (Array.isArray(o)) {
    let s = "[";
    for (let i = 0; i < o.length; i++) s += (i ? "," : "") + encodeValue(o[i], depth + 1);
    return s + "]";
  }
  if (ArrayBuffer.isView(o)) {
    if (o instanceof Uint8Array) return `{"$bytes":"${Buffer.from(o.buffer, o.byteOffset, o.byteLength).toString("base64")}"}`;
    if (o instanceof DataView) return `{"$bytes":"${Buffer.from(o.buffer, o.byteOffset, o.byteLength).toString("base64")}"}`;
    // Typed numeric arrays are arrays of numbers (Float32Array, Int32Array, BigInt64Array…).
    let s = "[";
    const a = o as unknown as ArrayLike<number | bigint>;
    for (let i = 0; i < a.length; i++) s += (i ? "," : "") + encodeValue(a[i], depth + 1);
    return s + "]";
  }
  if (o instanceof ArrayBuffer) return `{"$bytes":"${Buffer.from(o).toString("base64")}"}`;
  if (o instanceof Map) {
    let s = '{"$map":[';
    let first = true;
    for (const [k, x] of o) {
      s += (first ? "[" : ",[") + encodeKey(typeof k === "string" ? k : String(k)) + "," + encodeValue(x, depth + 1) + "]";
      first = false;
    }
    return s + "]}";
  }
  if (o instanceof Set) {
    let s = "[";
    let first = true;
    for (const x of o) {
      s += (first ? "" : ",") + encodeValue(x, depth + 1);
      first = false;
    }
    return s + "]";
  }
  if (o instanceof Error) return encodeString(`${o.name}: ${o.message}`);
  if (o instanceof URL || o instanceof RegExp) return encodeString(String(o));
  if (!isPlainObject(o) && typeof (o as { toJSON?: unknown }).toJSON === "function") {
    return encodeValue((o as { toJSON(): unknown }).toJSON(), depth + 1);
  }
  // Plain objects (and other instances: their own enumerable properties) are ordered maps.
  let s = '{"$map":[';
  let first = true;
  for (const k in o) {
    if (!Object.prototype.hasOwnProperty.call(o, k)) continue;
    s += (first ? "[" : ",[") + encodeKey(k) + "," + encodeValue((o as Record<string, unknown>)[k], depth + 1) + "]";
    first = false;
  }
  return s + "]}";
}

/** Attributes in their order: an object, a `Map`, or `[key, value]` pairs (duplicates kept). `undefined` values are skipped. */
export type Attributes = Readonly<Record<string, unknown>> | ReadonlyMap<string, unknown> | ReadonlyArray<readonly [string, unknown]>;

/** `["k",v],["k",v]` (no surrounding brackets); empty when there is nothing to write. */
export function encodePairs(attrs: Attributes): string {
  let s = "";
  if (Array.isArray(attrs)) {
    for (const [k, v] of attrs as ReadonlyArray<readonly [string, unknown]>) {
      if (v === undefined) continue;
      s += (s ? ",[" : "[") + encodeKey(k) + "," + encodeValue(v) + "]";
    }
  } else if (attrs instanceof Map) {
    for (const [k, v] of attrs) {
      if (v === undefined) continue;
      s += (s ? ",[" : "[") + encodeKey(String(k)) + "," + encodeValue(v) + "]";
    }
  } else {
    const o = attrs as Record<string, unknown>;
    for (const k in o) {
      const v = o[k];
      if (v === undefined) continue;
      s += (s ? ",[" : "[") + encodeKey(k) + "," + encodeValue(v) + "]";
    }
  }
  return s;
}
