"""Driven by tests/LatticeLog.Tests/SourcesTests.cs. Usage:
    python -I python_local.py <sdk-path> <service-folder>
Writes a structlog service's logs in local mode from two concurrent writers (two process folders). Exit 3 when
structlog is not installed."""
import json
import sys

sys.path.insert(0, sys.argv[1])
import latticelog  # noqa: E402

try:
    import structlog
except ImportError:
    sys.exit(3)


class QuotaExceeded(Exception):
    pass


folder = sys.argv[2]
first = latticelog.open(folder, service="py-svc", environment="test", durability="durable")
second = latticelog.open(folder, service="py-svc", environment="test")  # a second writer: its own p-2 folder
for target, n in ((first, 30), (second, 10)):
    structlog.configure(processors=[structlog.processors.add_log_level, structlog.processors.TimeStamper(fmt="iso"),
                                    latticelog.structlog_processor(target, drop=True)])
    log = structlog.get_logger()
    for i in range(n):
        log.info("request_completed", i=i)
    if target is first:
        log.info("order_placed", items=7, total=19.5)
        try:
            json.loads("{oops")
        except ValueError:
            log.exception("parse_failed", source="upload")
        for _ in range(1):
            try:
                raise QuotaExceeded("tenant 9 over quota")
            except QuotaExceeded:
                log.error("quota_check", exc_info=True)
    else:
        try:
            raise QuotaExceeded("tenant 3 over quota")
        except QuotaExceeded:
            log.error("quota_check", exc_info=True)
first.close()
second.close()
