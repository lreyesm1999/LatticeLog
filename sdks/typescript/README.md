# LatticeLog SDK for Node.js / TypeScript (P21, alpha)

Typed, structured records written **straight into LatticeLog's format in a local folder** (found by service name by
the `latticelog` CLI and MCP server), optionally **shipped to `latticelog-server`** with ingest protocol v1. ESM with
type declarations, Node.js ≥ 20, no runtime dependencies (the winston entry point uses `winston-transport`, which
winston already installs).

The logging call stays out of the application's way: it encodes the record into one string and appends it to an
in-memory queue. It never touches a file. The writer runs on the event loop in batches (every 10 ms in Balanced mode,
or at once past 512 Ki characters), assigns sequences and appends with positional writes on libuv's thread pool.
fsync runs there too. Measured cost: see [Performance](#performance).

Not on npm yet (see `ROADMAP.md`). Build it from the repository:

```text
cd sdks/typescript && npm ci          # installs the dev tools and builds dist/ (the "prepare" script)
cd your-app && npm install ../path/to/LatticeLog/sdks/typescript
```

## Local mode: one folder per service, found by name

```ts
import * as ll from "latticelog";

const log = ll.open("logs/orders-api", { service: "orders-api", environment: "dev" });

log.info("order {id} placed", { id: 991, total: 19.99, "http.status": 200 });

log.error("payment failed", { order_id: 991 }, {
  event: "payment_failed",                        // the identity LatticeLog groups and queries by
  error: err,                                     // real type (class name), stack, cause chain
  traceId: "4bf92f3577b34da6a3ce929d0e0e4736",
});

await log.operation("checkout", { id: "op-991", attributes: { cart: 7 } }).run(async () => {
  await chargeCard();
  log.info("inside");                             // operation_id = op-991, context chain Operation → Resource
});

await log.close();                                // writes everything, fsyncs, releases the process folder
```

- Records are official `latticelog.jsonl` v1 lines in `logs/orders-api/p-N/latticelog-<seq>.jsonl`, next to the
  manifest `latticelog.source.json`. This is the layout of the Python and Rust SDKs
  ([`specs/local-sources.md`](../../specs/local-sources.md)). The manifest says `"producer":"latticelog-typescript"`.
  Register the parent folder once with `latticelog roots add logs`. The CLI and MCP server then find `orders-api` by
  name, and `list_sources` reports it as `typescript-local`.
- Each live process takes its own `p-N` and continues that folder's dense sequence across restarts (see
  [Process folders](#process-folders-without-an-os-lock)). A torn last line from a crash is cut off when the folder is
  opened again.
- A new file is started at every start and at `fileMaxBytes` (16 MiB). Retention (`maxAgeMs`, `maxBytes`) deletes
  whole closed files, oldest first. It never deletes the file a live process writes.
- `open()` does synchronous file I/O once: the manifest, the process folder and recovery. Logging calls never do.

### Contexts follow async code

Contexts live in an `AsyncLocalStorage`. `ctx.run(fn)` makes `ctx` current for everything `fn` does, including
`await`, timers and callbacks. `ctx.enter()` (`enterWith`) is available too.

```ts
await log.scope("import", { file: "a.csv" }).run(async () => {
  log.info("started");                            // context chain: Scope → Resource
  await log.operation("row").run(async () => {    // random operation_id, parent_operation_id from the enclosing one
    log.warn("bad row", { line: 7 });
  });
});
log.info("explicit", null, { context: someContext });
```

The resource context holds `service.name`, `deployment.environment.name` and `resource` (for example
`{ "service.version": "1.4.2" }`).

### Types

JavaScript has one `number` type, so the SDK applies one rule: `Number.isSafeInteger(v)` → Int64, anything else →
Double. A Double is always written with a `.` or an exponent. The consequence is that `1.0` (the same value as `1`)
becomes Int64; wrap it in `f64(1)` to keep it a double.

| JavaScript | stored as |
|---|---|
| `number`, safe integer (except `-0`) | Int64 |
| `number`, otherwise | Double (`-0.0`, `2**53` → `9007199254740992.0`, NaN keeps its bits, ±Infinity) |
| `bigint` | Int64 when it fits, else UInt64, else Decimal |
| `string` | String; with unpaired surrogates as `$utf16le`, kept exactly |
| `boolean`, `null`, `undefined` | Bool, null, null (`undefined` attributes are skipped) |
| `Date` | Timestamp (millisecond precision; `timestamp(ns: bigint)` for nanoseconds) |
| `Uint8Array`, `Buffer`, `ArrayBuffer`, `DataView` | Bytes |
| `Map`, plain objects, other objects (own enumerable properties) | ordered Map |
| arrays, `Set`, numeric typed arrays | Array |
| `f64(n)`, `u64(n)`, `uuid(s)`, `decimal("19.990")`, `duration(ms \| ns)`, `timestamp(ns)`, `utf16(units)` | Double, UInt64, Uuid, Decimal (scale kept), Duration, Timestamp, String |

Values nest up to 32 levels (deeper values and cycles become `"[depth limit]"`). Keys with unpaired surrogates are
written with U+FFFD, as the Python SDK does.

### Errors

`{ error }` accepts anything thrown. An explicitly set `name` wins (`err.name = "ValidationError"`). Otherwise the
constructor's name is used, so `class PaymentError extends Error {}` is stored as `PaymentError`, not `Error`. The
message and `stack` are kept verbatim. `cause` and `AggregateError.errors` become inner exceptions (depth ≤ 8,
≤ 16 per level, cycles cut). A thrown non-Error is typed by `typeof` (`string`, `number`).

## pino and winston

```ts
import pino from "pino";
import { destination } from "latticelog/pino";
const logger = pino({ level: "info" }, destination(log));          // in-process, values stay typed
logger.child({ module: "billing" }).error({ err, order_id: 991, event: "payment_failed" }, "payment failed");
```

- **`latticelog/pino`** is a pino destination. pino serializes a call to JSON before any destination sees it, which
  loses types: `1.0` becomes `1`, a Date a string, a bigint a number. So the destination sets pino's metadata flag
  (`Symbol.for("pino.metadata")`, the mechanism `pino.multistream` uses) and reads the call's original object with its
  real values. The line is used only for what pino added: child bindings, `base`, mixins.
- **`latticelog/pino-transport`** runs in pino's worker thread:
  `pino.transport({ target: "latticelog/pino-transport", options: { folder, service } })`. The logging thread then
  does no LatticeLog work at all, but the worker only sees JSON, so values have their JSON types.
- **`latticelog/winston`**: `new LatticeLogTransport({ logger: log })`. winston hands transports the `info` object
  itself, so metadata keeps its types. `format.timestamp()` becomes the record time.

Both map fields the way the .NET importer maps pino/structlog JSON, so live and migrated logs answer the same
queries:

- `msg` / `message` → body.
- `event` (identifier-like) → event name. Free text becomes the message.
- An `err` / `error` holding an Error → exception.
- `trace_id` / `traceId` and `span_id` / `spanId` → trace fields.
- pino levels 10–60 → trace…fatal. winston npm and syslog level names are mapped by name.
- Every other key stays a typed attribute, in its order.

## Server mode

```ts
const log = ll.open("/var/lib/orders-api/logs", {
  service: "orders-api",
  server: { endpoint: "https://logs:7400", token: process.env.LATTICELOG_TOKEN!, namespace: "acme/prod" },
});
log.error("payment failed for {order}", { order: 991 });
await log.flushShipped(30_000); // true once the server confirmed (fsync) everything so far
```

The folder is the durable outbox. A background loop ships each process folder's own lines **byte for byte**: a header
and the context lines the records reference are added, and nothing is decoded or re-encoded. Batches are gzipped,
CRC-32 checked and sent in the 48-byte `LLIB` envelope of [`specs/ingest-protocol.md`](../../specs/ingest-protocol.md).

- **Guarantees**: every record has a dense local sequence, and the server deduplicates by (producer, sequence). Only
  the server's acknowledgement moves `p-N/cursor.json`. Confirmed closed files are deleted; `keepAcknowledged: true`
  keeps them for local queries, under the retention options. Without a network, everything waits in the folder,
  across restarts.
- **Protocol answers**: 5xx, 429 and network errors retry with exponential backoff and full jitter, honouring
  `Retry-After`. 413 halves the batch. 409 `sequence_gap` resends from the server's cursor, and declares the gap when
  those records are gone. 400/401/403 stop shipping (`stats().shipFailed`, `shipError`) and keep the records.
- **Backlog**: at `maxSpoolBytes` (512 MiB) of unconfirmed files, new records are refused and counted
  (`droppedSpoolFull`). Nothing unconfirmed is ever deleted.
- An idle shipper never keeps the process alive: it waits on unreferenced timers. `flushShipped()` and `close()` keep
  the process alive while they wait. Call one of them before exiting if the records should leave now rather than at
  the next start.

## Guarantees and counters

Writing is asynchronous in every mode, as in the .NET logger ([`specs/durability.md`](../../specs/durability.md)). A
logging call returning `true` means *accepted in memory*, never "persisted". `await log.flush({ fsync })` waits for
everything accepted before the call. It reports the level actually reached (`WrittenToOs`, `FsyncConfirmed`) and any
write error.

| durability | writer | fsync |
|---|---|---|
| `fast` | batches every ≤ 100 ms | at flush, rotation and close |
| `balanced` (default) | batches every ≤ 10 ms | every second |
| `durable` | at once (`setImmediate`), one batch at a time | every batch, before `flush()` reports |

- **Saturation**: the queue holds `queueChars` (16 Mi characters) of encoded records. When it is full, `"drop"` (the
  default) drops the new record and counts it in `droppedSaturation`. `"block"` makes that one call write the queue
  out synchronously instead: lossless, but the call blocks. Node.js cannot wait for a writer without blocking the
  event loop, so Rust's timed `Block` has no equivalent here.
- **Every loss is counted** in `stats()`: `droppedSaturation`, `droppedWriteError`, `droppedSpoolFull`,
  `droppedAfterClose`. Records below `minLevel` are not counted.
- **fsync is never on the logging call**. Rotated files are fsynced one at a time on the thread pool. In parallel,
  16 MiB fsyncs of about 250 ms each on an HDD would fill libuv's four threads and stall the writes. The periodic
  Balanced fsync also runs on the pool. `flush({ fsync: true })`, `close()` and Durable mode wait for all of them
  before they report `FsyncConfirmed`.
- **Write errors**: the file is cut back to the start of the failed write. Its records, and the rest of that batch,
  are counted as dropped and their sequences are reused, so the folder never has a gap or a duplicate. The next record
  starts a new file.
- **Exit**: records still queued are written synchronously from a `process.on("exit")` hook (`exitHook: true`, the
  default). This covers `process.exit()`, the end of the event loop and uncaught exceptions; Durable mode also fsyncs
  there. Writes still running on the thread pool are written again at the same offsets, because the process may end
  before they complete. A signal that kills the process (SIGKILL, an unhandled SIGTERM) runs no hook: handle SIGTERM
  and `await log.close()`.
- `await using log = ll.open(…)` closes the logger at the end of the block.

## Process folders without an OS lock

Python and Rust hold an OS lock on `p-N/LOCK`. Node.js has no portable file lock, so a process claims a folder by
creating `LOCK` exclusively (`wx`) with `{pid, host, started, token}`. It deletes the file at `close()` or exit. A
`LOCK` that already exists is taken over only when its owner is certainly gone:

- **Same host, another pid**: `process.kill(pid, 0)` says the process no longer exists.
- **Same pid**: the recorded start time differs from this process's by more than 2 s. This catches a container that
  restarted with the same pid 1. Another logger or worker thread of this process is never taken over.
- A takeover re-reads the file under a guard file (`LOCK.takeover`, created exclusively), so two processes never both
  take the same stale folder.

Everything uncertain counts as alive. That costs at most a new `p-N`, never two writers in one folder:

- A `LOCK` from another host (a network folder).
- An empty `LOCK`, which is how the Python and Rust SDKs leave theirs.
- A pid that the OS reused for an unrelated process.

The limits:

- The folder count can grow by one per unclean exit whose pid was reused, or per container crash loop that restarts
  within 2 s.
- A Python or Rust process does not see this LOCK. Do not mix SDKs in one service folder. The manifest names one
  producer, and different services use different folders anyway.
- A guard file left by a process that died during a takeover is ignored after 10 s.

## Performance

One machine: i9-14900KF, Windows 11, files on a 7,200 rpm HDD, Node.js 26.13.1, process affinity 0xFF (8 logical
CPUs, because the machine is unstable under all-core load). One run per row, repeated once. Method, every row and what
was not measured: [`reports/typescript-sdk-v1.md`](../../reports/typescript-sdk-v1.md). The same record in every row
(message + 3 attributes), compared with pino writing JSON to a file:

| per call, 500,000 records | ns per call | p50 / p99 (single calls) |
|---|---:|---:|
| `log.info(msg, attrs)` (Balanced) | 350 | 300 / 1,000 ns |
| pino → `pino.destination({ sync: false })` | 1,251 | 900 / 3,000 ns |
| pino → `pino.destination({ sync: true })` | 2,332 | — |
| pino → `pino.transport` (`pino/file`, worker thread) | 418 | — |
| pino → `latticelog/pino` destination | 1,707 | — |

A disabled level costs 1.7 ns per call (pino 2.8 ns). The attribute object is still built, because JavaScript
evaluates arguments before the call. Guard expensive attributes with `log.isEnabled("debug")`.

**Flood test.** 500,000 records were offered as fast as possible, with the event loop yielding every 1,000 records
(about 2.5 M records/s, 480 MB/s).

- On the NVMe SSD the writer kept up: 1.6 M records/s end to end against pino's 0.53 M, no drops, and the queue
  peaked at 5,000 records.
- On the HDD the writer stalled, and records past the 16 Mi-character queue were dropped and counted:
  - Balanced: 6.9 % in both runs;
  - Fast: 0 % in one run, 22.6 % in the other.
- One stall cause is measured. On NTFS, an append waits while the same file is fsynced, so the periodic Balanced
  fsync paused the writer for 284–370 ms per 16 MiB. The Fast-mode stall was not isolated.
- pino never fsyncs, and it buffers without a bound instead: it needed 0.8–1.4 s after the last call to write its
  backlog.
- Use `saturation: "block"` to lose nothing (950 ns per call in that test).
- At a steady 100,000 records/s, nothing was dropped on the HDD.

## Limitations

- No schema-declared events yet: the event name goes in `{ event }`.
- Contexts are process-local: they are not transmitted across processes (use `traceId`, `operation_id`,
  `correlationId`).
- `"block"` saturation is the only case where a logging call writes to a file.
- A `Date` has millisecond precision. Record times use the wall clock re-anchored every second, with the
  sub-millisecond part from the monotonic clock.
- The pino transport (worker thread) sees JSON only: use the destination for exact types.

## Tests

```text
npm test        # tsc, then node:test: values, local mode, server mode (fake ingest server), pino, winston
npm run bench -- D:/bench 500000
```

Cross-language tests against the real server and the MCP catalog: `tests/LatticeLog.Tests/SdkTests.cs` and
`SourcesTests.cs`. They build this package (running `npm ci` first when TypeScript is missing) and run
`examples/smoke.mjs` with the `node` on PATH. They are skipped when node is not installed.
