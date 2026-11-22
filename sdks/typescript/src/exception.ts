// Exceptions (specs/event-model.md §7): type, message, stack text and inner causes, depth ≤ 8, ≤ 16 inner per level.

import { encodeString } from "./values.js";

const MAX_DEPTH = 8;
const MAX_INNER = 16;

/**
 * The real type of an error: an explicitly set `name` (`err.name = "ValidationError"`, or a class that sets it) wins;
 * otherwise the constructor's name, so `class PaymentError extends Error {}` is `PaymentError`, not `Error`.
 */
export function errorType(e: unknown): string {
  if (e instanceof Error || (typeof e === "object" && e !== null)) {
    const name = (e as { name?: unknown }).name;
    if (typeof name === "string" && name && name !== "Error") return name;
    const ctor = (e as { constructor?: { name?: unknown } }).constructor?.name;
    if (typeof ctor === "string" && ctor && ctor !== "Object") return ctor;
    return typeof name === "string" && name ? name : "Error";
  }
  return typeof e; // a thrown string, number…
}

/** `{"type":…,"message":…,"stack":…,"inner":[…]}` with the `cause` chain and `AggregateError.errors` as inner exceptions. */
export function encodeException(e: unknown, depth = 0, seen: Set<unknown> = new Set()): string {
  seen.add(e);
  let s = '{"type":' + encodeString(errorType(e));
  if (e instanceof Error || (typeof e === "object" && e !== null && "message" in e)) {
    const err = e as { message?: unknown; stack?: unknown; cause?: unknown; errors?: unknown };
    if (err.message !== undefined && err.message !== "") s += ',"message":' + encodeString(String(err.message));
    if (typeof err.stack === "string" && err.stack) s += ',"stack":' + encodeString(err.stack);
    if (depth + 1 < MAX_DEPTH) {
      const inner: unknown[] = [];
      if (Array.isArray(err.errors)) for (const x of err.errors) if (inner.length < MAX_INNER && !seen.has(x)) inner.push(x);
      if (err.cause !== undefined && inner.length < MAX_INNER && !seen.has(err.cause)) inner.push(err.cause);
      if (inner.length) s += ',"inner":[' + inner.map((x) => encodeException(x, depth + 1, seen)).join(",") + "]";
    }
  } else if (e !== undefined && e !== null) {
    s += ',"message":' + encodeString(String(e));
  }
  return s + "}";
}
