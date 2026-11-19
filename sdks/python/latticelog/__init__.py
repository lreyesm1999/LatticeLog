"""LatticeLog SDK for Python (P21): structured logging into a local LatticeLog folder, or to a durable spool shipped
to latticelog-server.

Local mode (no server): the service writes official latticelog.jsonl v1 records into its own folder; the
latticelog CLI/MCP finds the folder by service name (specs/local-sources.md).

    import latticelog, structlog
    log = latticelog.open("logs/orders-api", service="orders-api", environment="dev")
    structlog.configure(processors=[structlog.processors.add_log_level, structlog.processors.TimeStamper(fmt="iso"),
                                    latticelog.structlog_processor(log), structlog.dev.ConsoleRenderer()])

Server mode:

    import latticelog
    log = latticelog.Logger("./spool", endpoint="https://logs:7400", token=os.environ["LATTICELOG_TOKEN"],
                            service="orders-api", namespace="acme/prod")
    with log.operation("checkout", operation_id="op-991"):
        log.error("payment failed for {order}", order=991, amount=19.99)
    log.flush()          # waits until the server acknowledged everything written so far

Standard library only. See sdks/python/README.md."""
from __future__ import annotations

import contextlib
import contextvars
import logging
import os
import time

from ._jsonl import SEVERITY, _ts, encode_attributes, encode_value, time_nanos
from ._local import LocalStore
from ._sender import Sender
from ._spool import Spool, SpoolFull
from ._structlog import exception_record, structlog_processor, write_event_dict

__all__ = ["Logger", "LatticeLogHandler", "LocalStore", "Spool", "SpoolFull", "open", "structlog_processor", "__version__"]
__version__ = "0.1.0a1"

_context: contextvars.ContextVar = contextvars.ContextVar("latticelog_context", default=None)
_operation: contextvars.ContextVar = contextvars.ContextVar("latticelog_operation", default=None)


class Logger:
    def __init__(self, spool_dir: str, endpoint: str | None = None, token: str | None = None, *, service: str | None = None,
                 namespace: str | None = None, resource: dict | None = None, durability: str = "balanced",
                 min_level: str = "info", max_spool_bytes: int = 512 * 1024 * 1024, batch_records: int = 1000,
                 poll_interval: float = 0.2, on_error=None):
        self._setup(Spool(spool_dir, max_spool_bytes, durability), service, resource, min_level)
        self.sender = Sender(self.spool, endpoint, token, namespace, batch_records, poll_interval, on_error=on_error) \
            if endpoint and token else None

    def _setup(self, spool, service, resource, min_level, environment=None):
        self.spool = spool
        attrs = dict(resource or {})
        if service:
            attrs.setdefault("service.name", service)
        if environment:
            attrs.setdefault("deployment.environment.name", environment)
        self._root = {"kind": "Resource", "attributes": encode_attributes(attrs)} if attrs else None
        self.min_level = SEVERITY[min_level]
        self.sender = None

    @classmethod
    def _local(cls, store: LocalStore, resource, min_level) -> "Logger":
        self = cls.__new__(cls)
        self._setup(store, store.service, resource, min_level, store.environment)
        return self

    # ------------------------------------------------------------------ contexts

    @contextlib.contextmanager
    def scope(self, name: str | None = None, **attributes):
        parent = _context.get() or self._root
        ctx = {"kind": "Scope", "name": name, "attributes": encode_attributes(attributes)}
        if parent is not None:
            ctx["parent"] = parent
        token = _context.set(ctx)
        try:
            yield
        finally:
            _context.reset(token)

    @contextlib.contextmanager
    def operation(self, name: str, operation_id: str | None = None, **attributes):
        parent = _context.get() or self._root
        op_id = operation_id or os.urandom(8).hex()
        ctx = {"kind": "Operation", "name": name, "attributes": encode_attributes({"operation.name": name, **attributes})}
        if parent is not None:
            ctx["parent"] = parent
        prev_op = _operation.get()
        t1, t2 = _context.set(ctx), _operation.set((op_id, prev_op[0] if prev_op else None))
        try:
            yield op_id
        finally:
            _context.reset(t1)
            _operation.reset(t2)

    # ------------------------------------------------------------------ writing

    def log(self, level: str, message: str | None = None, *, event: str | None = None, exc: BaseException | None = None,
            trace_id: str | None = None, span_id: str | None = None, time=None, **attributes) -> int | None:
        """Writes one record. Returns its local sequence, or None when filtered or refused (spool full)."""
        return self._emit(level, message, event, exception_record(exc) if exc is not None else None, trace_id, span_id,
                          time_nanos(time) if time is not None else None, attributes)

    def _emit(self, level: str, message, event: str | None, exception: dict | None, trace_id: str | None,
              span_id: str | None, nanos: int | None, attributes: dict) -> int | None:
        """Low-level write: attribute names never collide with parameters (structlog dicts may hold 'time', 'event')."""
        sev = SEVERITY[level]
        if sev < self.min_level:
            return None
        now = nanos if nanos is not None else _now_nanos()
        rec: dict = {"type": "record", "time": _ts(now), "severity_number": sev}
        if event:
            rec["event"] = event
        if message is not None:
            rec["body"] = encode_value(message)
        if attributes:
            rec["attributes"] = encode_attributes(attributes)
        if trace_id:
            rec["trace_id"] = trace_id.lower()
        if span_id:
            rec["span_id"] = span_id.lower()
        op = _operation.get()
        if op:
            rec["operation_id"] = op[0]
            if op[1]:
                rec["parent_operation_id"] = op[1]
        if exception is not None:
            rec["exception"] = exception
        ctx = _context.get() or self._root
        if ctx is not None:
            rec["ctx"] = ctx
        seq = self.spool.append(rec)
        if self.sender is not None and seq is not None:
            self.sender.wake()
        return seq

    def debug(self, message=None, **kw): return self.log("debug", message, **kw)
    def info(self, message=None, **kw): return self.log("info", message, **kw)
    def warn(self, message=None, **kw): return self.log("warn", message, **kw)
    def error(self, message=None, **kw): return self.log("error", message, **kw)
    def fatal(self, message=None, **kw): return self.log("fatal", message, **kw)

    @property
    def dropped(self) -> int:
        """Records refused because the spool was full (never silently lost)."""
        return self.spool.dropped

    def flush(self, timeout: float = 30.0) -> bool:
        """Persists the spool and, with a server, waits until it acknowledged everything written so far."""
        self.spool.flush(fsync=True)
        if self.sender is None:
            return True
        return self.sender.wait_for(self.spool.next_seq - 1, timeout)

    def close(self, timeout: float = 5.0):
        try:
            self.flush(timeout)
        finally:
            if self.sender is not None:
                self.sender.stop()
            self.spool.close()


def open(folder: str, service: str, environment: str | None = None, *, durability: str = "balanced", max_age=None,
         max_bytes: int | None = None, min_level: str = "info", resource: dict | None = None,
         file_max_bytes: int = 16 * 1024 * 1024) -> Logger:
    """Local mode, no server: writes official latticelog.jsonl v1 records into ``folder`` (one folder per service).

    durability: "fast" (OS buffers, flushed on flush/close/rotation), "balanced" (each record handed to the OS) or
    "durable" (fsync per record). max_age (seconds or timedelta) and max_bytes delete whole closed files, oldest first;
    the file a live process is writing is never deleted. Several processes may share ``folder``: each writes its own
    ``p-N`` subfolder, and LatticeLog shows them as one source."""
    store = LocalStore(folder, service, environment, durability, max_age, max_bytes, file_max_bytes)
    return Logger._local(store, resource, min_level)


def _now_nanos() -> int:
    return time.time_ns()


class LatticeLogHandler(logging.Handler):
    """Bridge for the standard logging module: logging.getLogger().addHandler(LatticeLogHandler(log))."""

    _LEVELS = [(logging.CRITICAL, "fatal"), (logging.ERROR, "error"), (logging.WARNING, "warn"), (logging.INFO, "info"), (logging.DEBUG, "debug")]
    _STANDARD = set(vars(logging.LogRecord("", 0, "", 0, "", None, None))) | {"message", "asctime"}

    def __init__(self, logger: Logger, level=logging.NOTSET):
        super().__init__(level)
        self.target = logger

    def emit(self, record: logging.LogRecord):
        try:
            level = next(name for threshold, name in self._LEVELS if record.levelno >= threshold) if record.levelno >= logging.DEBUG else "trace"
            if level == "trace":
                level = "debug"
            created = int(record.created * 1e9)
            extra = {k: v for k, v in record.__dict__.items() if k not in self._STANDARD and not k.startswith("_")}
            if isinstance(record.msg, dict) and "event" in record.msg:
                # structlog through ProcessorFormatter.wrap_for_formatter: msg is the event dict, not a format string.
                # getMessage() would flatten it to "{'event': ...}"; keep the event name and typed attributes instead.
                event_dict = {**extra, **record.msg}
                event_dict.setdefault("logger.name", record.name)
                if record.exc_info and not event_dict.get("exc_info"):
                    event_dict["exc_info"] = record.exc_info
                write_event_dict(self.target, event_dict, level=level, default_nanos=created)
                return
            extra["logger.name"] = record.name
            exc = record.exc_info[1] if record.exc_info else None
            self.target._emit(level, record.getMessage(), None, exception_record(exc) if exc is not None else None,
                              None, None, created, extra)
        except Exception:  # noqa: BLE001 - logging handlers must never raise
            self.handleError(record)
