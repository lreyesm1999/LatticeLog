# LatticeLog SDK for Python (P21, alpha)

Structured, typed records written **straight into LatticeLog's format in a local folder** (no server), or into a
**durable local spool** shipped to `latticelog-server` in the background with ingest protocol v1. Standard library
only (Python ≥ 3.10).

## Local mode: one folder per service, found by name

```python
import latticelog, structlog

log = latticelog.open("logs/orders-api", service="orders-api", environment="dev")   # no server
structlog.configure(processors=[
    structlog.contextvars.merge_contextvars,
    structlog.processors.add_log_level,
    structlog.processors.TimeStamper(fmt="iso"),
    latticelog.structlog_processor(log),      # the one added line; drop=True makes LatticeLog the only output
    structlog.dev.ConsoleRenderer(),
])
structlog.get_logger().info("order_placed", order_id=991, total=19.99)
```

- Records are official `latticelog.jsonl` v1 lines in `logs/orders-api/p-N/latticelog-<seq>.jsonl`, next to a
  manifest `latticelog.source.json` (service, environment, format version, producer id). Register the parent folder
  once with `latticelog roots add logs` (or a `latticelog.toml`) and the CLI/MCP server find the service by name.
- `event` becomes the event name (free text becomes the message), other keys stay typed attributes, and exceptions
  keep their real Python type (`httpx.HTTPStatusError`, `json.decoder.JSONDecodeError`) with traceback and causes.
- Several processes may share the folder: each writes its own `p-N` subfolder (shown as one source); a dense
  sequence per subfolder continues across restarts; a torn last line from a crash is cut off.
- Same durability modes as the spool. Retention: `max_age=` (seconds or `timedelta`) and `max_bytes=` delete whole
  closed files, oldest first; the file a live process is writing is never deleted.
- Without structlog: `log.info("order {id}", id=1, event="order_placed")`, or the `logging` bridge below.
  `LatticeLogHandler` also understands structlog's `ProcessorFormatter.wrap_for_formatter` (the event dict is kept,
  not flattened to text).

Details: [`specs/local-sources.md`](../../specs/local-sources.md).

## Server mode

```python
import os, latticelog

log = latticelog.Logger("./spool", endpoint="https://logs:7400", token=os.environ["LATTICELOG_TOKEN"],
                        service="orders-api", namespace="acme/prod", durability="balanced")
with log.operation("checkout", operation_id="op-991"):
    log.error("payment failed for {order}", order=991, amount=19.99)
log.flush()   # waits until the server has confirmed (fsync) everything written
log.close()
```

With the `logging` module: `logging.getLogger().addHandler(latticelog.LatticeLogHandler(log))`.

- **Guarantees**: every record gets a dense local sequence. The sender retries with backoff and jitter, and the server
  deduplicates by (producer, sequence). Without a network, everything waits in the spool, across process restarts too.
  Only what the server has confirmed is deleted.
- **Durability**: `fast` (buffered), `balanced` (flush per record) or `durable` (fsync per record).
- **Full spool** (`max_spool_bytes`): new records are rejected and counted in `log.dropped`. Nothing unconfirmed is
  ever deleted.
- **Types**: `int` → Int64 (or UInt64/decimal when it does not fit), `float` → Double (NaN and infinities included),
  `bytes`, `datetime`, `timedelta`, `UUID`, `Decimal` (with its scale), `dict` (ordered map), lists. Strings with lone
  surrogates are preserved exactly.
- **Limitations**: one writer process per spool (for several processes, use one spool per process or the PHP SDK).
  There are no schema-declared events yet: the event name goes in `event=`.

Tests: `python -m unittest discover -s sdks/python/tests` (local mode, structlog). Integration tests:
`tests/LatticeLog.Tests/SdkTests.cs` (`tests/sdk/python_smoke.py`, against a real server) and
`tests/LatticeLog.Tests/SourcesTests.cs` (`tests/sdk/python_local.py`, local mode found by name through MCP).
