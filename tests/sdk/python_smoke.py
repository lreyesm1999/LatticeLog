"""Driven by tests/LatticeLog.Tests/SdkTests.cs. Usage:
    python -I python_smoke.py <sdk-path> offline <spool>                       # write 300 records, no server
    python -I python_smoke.py <sdk-path> ship <spool> <endpoint> <token>       # write 200 more, ship everything
"""
import datetime
import decimal
import sys
import uuid

sys.path.insert(0, sys.argv[1])
import latticelog  # noqa: E402

mode, spool = sys.argv[2], sys.argv[3]
if mode == "offline":
    log = latticelog.Logger(spool, service="py-orders", durability="durable")
    for i in range(300):
        log.info("order {i}", i=i)
    log.close()
    sys.exit(0)

endpoint, token = sys.argv[4], sys.argv[5]
log = latticelog.Logger(spool, endpoint=endpoint, token=token, service="py-orders", namespace="acme/prod", poll_interval=0.02)
for i in range(300, 500):
    if i == 300:
        with log.operation("checkout", operation_id="op-300", cart=7):
            try:
                try:
                    raise KeyError("sku")
                except KeyError as inner:
                    raise ValueError("payment failed") from inner
            except ValueError as e:
                log.error("special {i}", i=i, exc=e, ratio=1.0, big=2 ** 63, nan=float("nan"), raw=b"\x00\xff",
                          when=datetime.datetime(2026, 1, 2, 3, 4, 5, 123456, tzinfo=datetime.timezone.utc),
                          id=uuid.UUID(int=255), price=decimal.Decimal("19.990"), nested={"a": [1, 2.5, "x"]},
                          odd="a\ud800b", unicode="héllo 世界 🎉", trace_id="4bf92f3577b34da6a3ce929d0e0e4736")
    else:
        log.info("order {i}", i=i)
ok = log.flush(30)
print("shipped" if ok else f"not shipped: {log.sender.last_error}")
log.close()
sys.exit(0 if ok else 1)
