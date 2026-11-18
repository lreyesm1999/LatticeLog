"""LatticeLog SDK for Python (P21): structured logging to a local durable spool, shipped to latticelog-server.

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
import traceback

from ._jsonl import SEVERITY, _ts, encode_attributes, encode_value, time_nanos
from ._sender import Sender
from ._spool import Spool, SpoolFull

__all__ = ["Logger", "LatticeLogHandler", "Spool", "SpoolFull", "__version__"]
__version__ = "0.1.0a1"

_context: contextvars.ContextVar = contextvars.ContextVar("latticelog_context", default=None)
_operation: contextvars.ContextVar = contextvars.ContextVar("latticelog_operation", default=None)


class Logger:
    def __init__(self, spool_dir: str, endpoint: str | None = None, token: str | None = None, *, service: str | None = None,
                 namespace: str | None = None, resource: dict | None = None, durability: str = "balanced",
                 min_level: str = "info", max_spool_bytes: int = 512 * 1024 * 1024, batch_records: int = 1000,
                 poll_interval: float = 0.2, on_error=None):
        self.spool = Spool(spool_dir, max_spool_bytes, durability)
        attrs = dict(resource or {})
        if service:
            attrs.setdefault("service.name", service)
        self._root = {"kind": "Resource", "attributes": encode_attributes(attrs)} if attrs else None
        self.min_level = SEVERITY[min_level]
        self.sender = Sender(self.spool, endpoint, token, namespace, batch_records, poll_interval, on_error=on_error) \
            if endpoint and token else None

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
        sev = SEVERITY[level]
        if sev < self.min_level:
            return None
        now = time_nanos(time) if time is not None else _now_nanos()
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
        if exc is not None:
            rec["exception"] = _exception(exc)
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


def _now_nanos() -> int:
    return time.time_ns()


def _exception(exc: BaseException) -> dict:
    out = {"type": f"{type(exc).__module__}.{type(exc).__qualname__}".removeprefix("builtins."), "message": str(exc)}
    stack = "".join(traceback.format_tb(exc.__traceback__)) if exc.__traceback__ else None
    if stack:
        out["stack"] = stack
    inner = exc.__cause__ or exc.__context__
    if inner is not None and inner is not exc:
        out["inner"] = [_exception(inner)]
    return out


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
            extra = {k: v for k, v in record.__dict__.items() if k not in self._STANDARD and not k.startswith("_")}
            extra["logger.name"] = record.name
            exc = record.exc_info[1] if record.exc_info else None
            self.target.log(level, record.getMessage(), event=None, exc=exc, time=int(record.created * 1e9), **extra)
        except Exception:  # noqa: BLE001 - logging handlers must never raise
            self.handleError(record)
