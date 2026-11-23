# LatticeLog SDK for Rust (P21, alpha)

Typed, structured records written **straight into LatticeLog's format in a local folder** (found by service name by
the `latticelog` CLI and MCP server), optionally **shipped to `latticelog-server`** with ingest protocol v1. Rust ≥ 1.89.

The hot path is built to stay out of the application's way: the calling thread encodes the record into a reused
thread-local buffer (no allocation once warm) and copies the bytes into a shared batch under a short lock. A background
thread assigns sequences, resolves contexts and appends whole batches with one `write`. Measured cost: see
[Performance](#performance).

```toml
[dependencies]
latticelog = { path = "sdks/rust" }                                  # tracing + log bridges (default)
# latticelog = { path = "sdks/rust", features = ["server"] }        # + shipping over HTTP
# latticelog = { path = "sdks/rust", features = ["tls"] }           # + HTTPS endpoints (rustls)
```

Not on crates.io yet (see `ROADMAP.md`).

## Local mode: one folder per service, found by name

```rust
use latticelog::{Options, Level};

let log = latticelog::open("logs/orders-api", Options::new("orders-api").environment("dev"))?;
let _guard = log.guard(); // writes everything and fsyncs when main returns

latticelog::info!(log, "order {id} placed", id = 991, total = 19.99, "http.status" = 200);

log.error("payment failed")
    .event("payment_failed")              // the identity LatticeLog groups and queries by
    .attr("order_id", 991)
    .trace_hex("4bf92f3577b34da6a3ce929d0e0e4736", None)
    .error(&err)                          // real type (std::any::type_name) + source() chain
    .emit();

{
    let _op = log.operation("checkout", Some("op-991")).attr("cart", 7).enter();
    latticelog::info!(log, "inside");     // operation_id = op-991, context chain Operation → Resource
}
```

- Records are official `latticelog.jsonl` v1 lines in `logs/orders-api/p-N/latticelog-<seq>.jsonl`, next to the
  manifest `latticelog.source.json` — the same layout as the Python SDK ([`specs/local-sources.md`](../../specs/local-sources.md)).
  Register the parent folder once with `latticelog roots add logs` and the CLI/MCP server find `orders-api` by name;
  `list_sources` reports it as `rust-local`.
- Several processes may share the folder: each takes its own `p-N` (an OS lock on `p-N/LOCK`, released when the
  process dies) and continues that folder's dense sequence across restarts. A torn last line from a crash is cut off.
- A new file is started at every start and at `file_max_bytes` (16 MiB). Retention (`max_age`, `max_bytes`) deletes
  whole closed files, oldest first; the file a live process writes is never deleted.

### `tracing`

```rust
use tracing_subscriber::prelude::*;
tracing_subscriber::registry().with(latticelog::layer(&log)).init();

let span = tracing::info_span!("checkout", operation_id = "op-991", cart = 7);
let _e = span.enter();
tracing::error!(event = "payment_failed", order_id = 991, error = &err as &dyn std::error::Error, "payment failed");
```

Spans become contexts (`Scope`, or `Operation` when the span has an `operation_id` field) and follow futures, so this
is the way to get contexts in `async` code. Event fields stay typed attributes; `message` is the body, `event`
(identifier-like) the event name, `trace_id`/`span_id` (hex) the trace fields, `correlation_id`, and an error value
the exception. The target is kept as `logger.name`.

### `log`

```rust
latticelog::init_log(&log)?;
log::info!(order_id = 991, total = 19.99; "order {} placed", 991);   // key-values stay typed
```

## Server mode

```rust
use latticelog::{Options, ServerOptions};

let log = latticelog::open("/var/lib/orders-api/logs", Options::new("orders-api").server(
    ServerOptions::new("https://logs:7400", std::env::var("LATTICELOG_TOKEN")?).namespace("acme/prod"),
))?;
latticelog::error!(log, "payment failed for {order}", order = 991);
log.flush_shipped(std::time::Duration::from_secs(30)); // true once the server confirmed (fsync) everything so far
```

The folder is the durable outbox: a background thread ships each process folder's own lines **byte for byte** (a
header and the context lines they reference are added; nothing is decoded or re-encoded), gzip, CRC-32, in the 48-byte
`LLIB` envelope of [`specs/ingest-protocol.md`](../../specs/ingest-protocol.md).

- **Guarantees**: every record has a dense local sequence; the server deduplicates by (producer, sequence); only its
  acknowledgement moves `p-N/cursor.json`; confirmed closed files are deleted (`keep_acknowledged()` keeps them for
  local queries, under the retention options). Without a network everything waits in the folder, across restarts.
- **Protocol answers**: 5xx, 429 and network errors retry with exponential backoff and full jitter (honouring
  `Retry-After`); 413 halves the batch; 409 `sequence_gap` resends from the server's cursor (declaring the gap when
  those records are gone); 400/401/403 stop shipping (`stats().ship_failed`, `ship_error`) and keep the records.
- **Backlog**: at `max_spool_bytes` (512 MiB) of unconfirmed files, new records are refused and counted
  (`dropped_spool_full`). Nothing unconfirmed is ever deleted.
- The process folder's lock is held until the shipper stopped: two processes never ship the same `p-N` at once.

## Guarantees and counters

Writing is asynchronous in every mode, like the .NET logger ([`specs/durability.md`](../../specs/durability.md)):
`emit()` returning `true` means *accepted in memory*, never "persisted". `flush(fsync)` waits for everything accepted
before the call and reports the level actually reached (`WrittenToOs`, `FsyncConfirmed`) and any write error.

| durability | writer | fsync |
|---|---|---|
| `Fast` | batches every ≤ 100 ms | at flush, rotation and close |
| `Balanced` (default) | batches every ≤ 10 ms (the OS timer may round up) | every second |
| `Durable` | woken by every record (group commit) | every batch |

- **Saturation** (`queue_bytes`, 16 MiB of encoded records): `Block(5 s)` by default, then drop; or `DropNewest`.
- **Every loss is counted** in `stats()`: `dropped_saturation`, `dropped_write_error`, `dropped_spool_full`,
  `dropped_after_close`. Records below the minimum level are not counted (they cost one atomic load).
- **Write errors**: the file is cut back to its last complete batch, the batch is counted as dropped and its
  sequences are reused, so the folder never has a gap or a duplicate; the next record starts a new file.
- **Exit**: keep `log.guard()` in `main`, or call `close()`. A logger that lives in a global subscriber is never
  dropped, so records still in memory at `process::exit` are lost without it. `log.log_panics()` writes panics as fatal
  records and flushes.

## Types

| Rust | stored as |
|---|---|
| `i8`…`i64`, `isize`, `u8`…`u32` | Int64 |
| `u64`, `usize`, `u128`, `i128` | Int64 when it fits, else UInt64, else Decimal |
| `f32`, `f64` | Double (`1.0` stays a double; NaN keeps its bits; infinities) |
| `bool`, `&str`, `String`, `char`, `fmt::Arguments`, `Option<T>` | Bool, String, null |
| `[T]`, `Vec<T>`, `[&dyn Value]` | Array |
| `BTreeMap`, `HashMap`, `Map(&[("k", &v)])` | ordered Map |
| `SystemTime`, `Timestamp(ns)` | Timestamp (9 fractional digits) |
| `Duration` | Duration |
| `Bytes(&[u8])`, `Uuid`, `Decimal("19.990")` | Bytes, Uuid, Decimal (scale kept) |
| `Utf16(&[u16])` | String; with unpaired surrogates kept exactly |
| `Display(x)`, `Debug(x)` | String, formatted without a temporary `String` |

## Performance

One machine (i9-14900KF, Windows 11, files on a 7,200 rpm HDD, 8 logical CPUs), one run per row; method, all rows
and what was not measured: [`reports/rust-sdk-v1.md`](../../reports/rust-sdk-v1.md). Same record in every row
(message + 3 typed attributes), compared with `tracing_subscriber::fmt().json()` through `tracing_appender::non_blocking`
(lossless):

| 1 thread | end to end records/s | ns per call |
|---|---:|---:|
| `log.info(..).attr(..).emit()` | 4.6 M | 216 |
| `tracing::info!` through `latticelog::layer` | 3.1 M | 321 |
| `fmt().json()` + `tracing-appender` | 0.62 M | 1,618 |

A disabled level costs 0.18 ns per call. Single-call latency at a rate the writer keeps up with: p50 200 ns, p99 400 ns
(clock resolution ~100 ns). `Durable` is bound by fsync (~200k records/s on that HDD).

## Limitations

- `Context::enter` is thread-local: in `async` code use the `tracing` layer or pass `.context(&ctx)`.
- A `dyn Error` (from `tracing`'s `record_error` or `anyhow`) has no nameable concrete type: common standard-library
  errors are recognized, others are typed `dyn std::error::Error`. `Record::error(&e)` with a concrete error keeps its
  real type.
- No fork handling (a forked child without `exec` must open its own logger).
- Records accepted but not yet written are lost if the process is killed (as with the .NET logger).
- No schema-declared events yet: the event name goes in `.event(...)`.

## Tests

```text
cargo test --features server          # unit tests, tests/local.rs, tests/server.rs (fake ingest server)
```

Cross-language tests against the real server and the MCP catalog: `tests/LatticeLog.Tests/SdkTests.cs` and
`SourcesTests.cs` build `examples/smoke.rs` with cargo (skipped when cargo is not installed).
