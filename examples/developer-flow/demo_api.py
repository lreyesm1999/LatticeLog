"""A small structlog service that writes its logs straight into LatticeLog format (no server, no agent).

    python demo_api.py            # writes logs/demo-api/ next to this file

The only LatticeLog lines are the two marked below."""
import os
import random
import sys

import structlog

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "..", "sdks", "python"))  # use the repo's SDK when not pip-installed
import latticelog  # noqa: E402


class UpstreamTimeout(Exception):
    """Raised when the inventory service does not answer in time."""


def main():
    log_store = latticelog.open(os.path.join(HERE, "logs", "demo-api"), service="demo-api", environment="dev")  # LatticeLog 1/2
    structlog.configure(processors=[
        structlog.contextvars.merge_contextvars,
        structlog.processors.add_log_level,
        structlog.processors.TimeStamper(fmt="iso"),
        latticelog.structlog_processor(log_store),  # LatticeLog 2/2
        structlog.dev.ConsoleRenderer(colors=False),
    ], logger_factory=structlog.PrintLoggerFactory(file=open(os.devnull, "w", encoding="utf-8")))  # console output muted for the demo
    log = structlog.get_logger()
    rnd = random.Random(7)
    for i in range(200):
        structlog.contextvars.bind_contextvars(request_id=f"req-{i}")
        log.info("request_completed", path=f"/orders/{i}", status=200, duration_ms=round(rnd.uniform(3, 40), 2))
        if i % 25 == 3:
            try:
                raise UpstreamTimeout(f"inventory did not answer for order {i}")
            except UpstreamTimeout:
                log.exception("inventory_call_failed", order_id=i, attempt=3)
        if i % 40 == 9:
            log.warning("Cache miss rate above threshold", rate=0.31)
    structlog.contextvars.clear_contextvars()
    log_store.close()
    print(f"demo-api wrote {log_store.spool.next_seq - 1} records into {log_store.spool.folder}")


if __name__ == "__main__":
    main()
