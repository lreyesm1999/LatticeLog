"""structlog integration: one processor turns structlog's event dict into a typed LatticeLog record.

    structlog.configure(processors=[..., latticelog.structlog_processor(log), structlog.dev.ConsoleRenderer()])

Mapping (the same rules the .NET importer applies to structlog JSON logs, so old and new logs query alike):
- ``event``: identifier-like values (``payment_failed``) become the event name; free text becomes the message body.
- ``level`` (or the method name) becomes the severity; ``timestamp`` (TimeStamper, ISO or epoch) the record time.
- ``exc_info`` keeps the real exception object: its type (``module.QualName``), message, traceback and causes.
  A pre-rendered ``exception`` string (``format_exc_info``) or list (``dict_tracebacks``) is parsed instead.
- ``trace_id``/``span_id`` (hex) map to the trace fields; every other key stays a typed attribute.
Standard library only; structlog itself is imported only for ``drop=True``."""
from __future__ import annotations

import datetime as _dt
import re
import sys
import traceback

from ._jsonl import time_nanos

_LEVELS = {"debug": "debug", "info": "info", "msg": "info", "log": "info", "warn": "warn", "warning": "warn",
           "error": "error", "err": "error", "exception": "error", "critical": "fatal", "fatal": "fatal",
           "trace": "trace", "notset": "debug"}
_EVENT_NAME = re.compile(r"^[A-Za-z_][A-Za-z0-9_.:/-]{0,127}$")
_HEX32 = re.compile(r"^[0-9a-fA-F]{32}$")
_HEX16 = re.compile(r"^[0-9a-fA-F]{16}$")
_DOTTED = re.compile(r"^[A-Za-z_][A-Za-z0-9_.]*$")


def exception_record(exc: BaseException) -> dict:
    """Exception object -> latticelog.jsonl exception (real type ``module.QualName``, builtins unprefixed)."""
    out = {"type": f"{type(exc).__module__}.{type(exc).__qualname__}".removeprefix("builtins."), "message": str(exc)}
    stack = "".join(traceback.format_tb(exc.__traceback__)) if exc.__traceback__ else None
    if stack:
        out["stack"] = stack
    inner = exc.__cause__ or exc.__context__
    if inner is not None and inner is not exc:
        out["inner"] = [exception_record(inner)]
    return out


def exception_from_text(text: str) -> dict:
    """Rendered Python traceback -> exception. The type is the first unindented line after the last frame."""
    lines = text.replace("\r\n", "\n").split("\n")
    if lines and lines[0].lstrip().startswith("Traceback (most recent call last)"):
        last_frame = max((i for i, l in enumerate(lines) if l.startswith("  File ")), default=-1)
        j = last_frame + 1
        while last_frame >= 0 and j < len(lines) and (not lines[j] or lines[j][0].isspace()):
            j += 1
        if last_frame >= 0 and j < len(lines):
            head, sep, rest = lines[j].partition(":")
            if _DOTTED.match(head.strip()):
                msg = "\n".join([rest.strip()] + lines[j + 1:]).strip() if sep else "\n".join(lines[j + 1:]).strip()
                out = {"type": head.strip(), "stack": text}
                if msg:
                    out["message"] = msg
                return out
        return {"type": "(unparsed)", "stack": text}
    head, sep, rest = lines[0].partition(":")
    out = {"type": head.strip() if sep and head.strip() else "(unparsed)", "stack": text}
    if sep and rest.strip():
        out["message"] = rest.strip()
    return out


def _exception_from_dicts(stacks: list) -> dict | None:
    """structlog.processors.dict_tracebacks output: outermost exception first, causes after it."""
    out = None
    for s in reversed(stacks):
        if not isinstance(s, dict) or "exc_type" not in s:
            return None
        rec = {"type": str(s["exc_type"]), "message": str(s.get("exc_value", ""))}
        frames = s.get("frames") or []
        if frames:
            rec["stack"] = "".join(f'  File "{f.get("filename")}", line {f.get("lineno")}, in {f.get("name")}\n' for f in frames if isinstance(f, dict))
        if out is not None:
            rec["inner"] = [out]
        out = rec
    return out


def _resolve_exc_info(v) -> BaseException | None:
    if isinstance(v, BaseException):
        return v
    if isinstance(v, tuple) and len(v) == 3:
        return v[1]
    if v is True:
        return sys.exc_info()[1]
    return None


def _parse_time(v) -> int | None:
    if isinstance(v, bool) or v is None:
        return None
    if isinstance(v, (int, float)):
        # TimeStamper(fmt=None) gives epoch seconds; larger magnitudes are ms/us/ns (same heuristic as the importer).
        if v > 1e17:
            return int(v)
        if v > 1e14:
            return int(v * 1_000)
        if v > 1e11:
            return int(v * 1_000_000)
        return int(v * 1_000_000_000)
    if isinstance(v, _dt.datetime):
        return time_nanos(v if v.tzinfo else v.astimezone())
    if isinstance(v, str):
        s = v.strip()
        if s.endswith("Z"):
            s = s[:-1] + "+00:00"
        try:
            d = _dt.datetime.fromisoformat(s)
        except ValueError:
            return None
        return time_nanos(d if d.tzinfo else d.astimezone())  # naive = local time (TimeStamper(utc=False))
    return None


def write_event_dict(target, event_dict: dict, *, level: str | None = None, method_name: str | None = None,
                     default_nanos: int | None = None) -> int | None:
    """Writes one structlog event dict to ``target`` (a latticelog.Logger). The dict is not modified."""
    d = dict(event_dict)
    raw_level = d.pop("level", None)
    sev = level or _LEVELS.get(str(raw_level or method_name or "info").lower(), "info")
    event = d.pop("event", None)
    name, message = None, None
    if isinstance(event, str) and _EVENT_NAME.match(event):
        name = event
    elif event is not None:
        message = event if isinstance(event, str) else str(event)
    nanos = None
    if "timestamp" in d:
        nanos = _parse_time(d["timestamp"])
        if nanos is not None:
            del d["timestamp"]
    exc = None
    exc_info = d.pop("exc_info", None)
    real = _resolve_exc_info(exc_info)
    if real is not None:
        exc = exception_record(real)
    rendered = d.get("exception")
    if exc is None and isinstance(rendered, str) and rendered:
        exc = exception_from_text(rendered)
        del d["exception"]
    elif exc is None and isinstance(rendered, list) and rendered:
        exc = _exception_from_dicts(rendered)
        if exc is not None:
            del d["exception"]
    elif exc is not None and isinstance(rendered, (str, list)):
        del d["exception"]  # the same exception, already rendered by format_exc_info/dict_tracebacks
    trace_id = d.pop("trace_id") if isinstance(d.get("trace_id"), str) and _HEX32.match(d["trace_id"]) else None
    span_id = d.pop("span_id") if isinstance(d.get("span_id"), str) and _HEX16.match(d["span_id"]) else None
    return target._emit(sev, message, name, exc, trace_id, span_id, nanos if nanos is not None else default_nanos, d)


def structlog_processor(log, *, drop: bool = False):
    """Returns a structlog processor that writes every event to ``log`` and passes the event dict on unchanged.

    Put it before the renderer: ``processors=[..., latticelog.structlog_processor(log), ConsoleRenderer()]``.
    With ``drop=True`` the event stops here (LatticeLog is the only output). Logging never raises into the app:
    a failure to write is counted in ``processor.errors``."""
    def processor(logger, method_name, event_dict):
        try:
            write_event_dict(log, event_dict, method_name=method_name)
        except Exception:  # noqa: BLE001 - logging must never break the application
            processor.errors += 1
        if drop:
            import structlog
            raise structlog.DropEvent
        return event_dict

    processor.errors = 0
    return processor
