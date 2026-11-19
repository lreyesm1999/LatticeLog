# LatticeLog SDK for Python (P21, alpha)

Structured, typed records in a **durable local spool**, shipped to `latticelog-server` in the background with ingest
protocol v1. Standard library only (Python ≥ 3.10).

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

Integration test against a real server: `tests/LatticeLog.Tests/SdkTests.cs` (`tests/sdk/python_smoke.py`).
