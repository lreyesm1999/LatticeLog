"""Lossless value encoding of latticelog.jsonl v1 (specs/jsonl-exchange.md) and the ingestion envelope
(specs/ingest-protocol.md). Standard library only."""
from __future__ import annotations

import base64
import datetime as _dt
import decimal
import gzip
import json
import math
import struct
import uuid
import zlib

INT64_MIN, INT64_MAX, UINT64_MAX = -(2 ** 63), 2 ** 63 - 1, 2 ** 64 - 1

SEVERITY = {"trace": 1, "debug": 5, "info": 9, "warn": 13, "warning": 13, "error": 17, "fatal": 21, "critical": 21}


def _has_lone_surrogate(s: str) -> bool:
    try:
        s.encode("utf-8")
        return False
    except UnicodeEncodeError:
        return True


def _ts(nanos: int) -> str:
    secs, frac = divmod(nanos, 1_000_000_000)
    base = _dt.datetime(1970, 1, 1, tzinfo=_dt.timezone.utc) + _dt.timedelta(seconds=secs)
    return base.strftime("%Y-%m-%dT%H:%M:%S") + f".{frac:09d}Z"


def time_nanos(value) -> int:
    """datetime (aware or naive UTC) or int nanoseconds -> Unix nanoseconds."""
    if isinstance(value, int):
        return value
    if value.tzinfo is None:
        value = value.replace(tzinfo=_dt.timezone.utc)
    delta = value - _dt.datetime(1970, 1, 1, tzinfo=_dt.timezone.utc)
    return (delta.days * 86_400 + delta.seconds) * 1_000_000_000 + delta.microseconds * 1_000


def encode_value(v):
    """Python value -> JSON-ready lossless form. Types keep their identity: 1 stays int64, 1.0 stays float64."""
    if v is None or isinstance(v, bool):
        return v
    if isinstance(v, int):
        if INT64_MIN <= v <= INT64_MAX:
            return v
        if 0 <= v <= UINT64_MAX:
            return {"$u64": str(v)}
        return {"$dec": str(v)}
    if isinstance(v, float):
        if math.isnan(v):
            return {"$f64bits": struct.pack(">d", v).hex().upper()}
        if math.isinf(v):
            return {"$f64": "Infinity" if v > 0 else "-Infinity"}
        return _Float(v)
    if isinstance(v, str):
        if _has_lone_surrogate(v):
            return {"$utf16le": base64.b64encode(v.encode("utf-16-le", "surrogatepass")).decode("ascii")}
        return v
    if isinstance(v, (bytes, bytearray, memoryview)):
        return {"$bytes": base64.b64encode(bytes(v)).decode("ascii")}
    if isinstance(v, _dt.datetime):
        return {"$ts": _ts(time_nanos(v))}
    if isinstance(v, _dt.timedelta):
        return {"$dur_ns": str((v.days * 86_400 + v.seconds) * 1_000_000_000 + v.microseconds * 1_000)}
    if isinstance(v, uuid.UUID):
        return {"$uuid": str(v)}
    if isinstance(v, decimal.Decimal):
        if not v.is_finite():
            return str(v)
        return {"$dec": str(v)}
    if isinstance(v, dict):
        return {"$map": [[clean_key(k), encode_value(x)] for k, x in v.items()]}
    if isinstance(v, (list, tuple, set, frozenset)):
        return [encode_value(x) for x in v]
    return str(v)


def clean_key(k) -> str:
    """Keys must be valid Unicode (the store sanitizes them otherwise); lone surrogates become U+FFFD."""
    k = str(k)
    return k.encode("utf-8", "replace").decode("utf-8") if _has_lone_surrogate(k) else k


def encode_attributes(attrs: dict) -> list:
    return [[clean_key(k), encode_value(v)] for k, v in attrs.items()]


class _Float(float):
    """Marks float64 so the encoder always writes a '.' or an exponent (1.0, -0.0, 1e+20)."""

    def __repr__(self):
        r = float.__repr__(self)
        if "e" not in r and "." not in r:
            r += ".0"
        return r


def dumps(obj) -> str:
    """Compact JSON; float64 values keep their marker; non-ASCII is escaped so lines are pure ASCII."""
    if isinstance(obj, _Float):
        return repr(obj)
    if isinstance(obj, dict):
        return "{" + ",".join(json.dumps(k) + ":" + dumps(v) for k, v in obj.items()) + "}"
    if isinstance(obj, list):
        return "[" + ",".join(dumps(x) for x in obj) + "]"
    return json.dumps(obj, ensure_ascii=True, allow_nan=False)


ENVELOPE_HEADER = 48
ENCODING_JSONL = 1
FLAG_GAP_ACKNOWLEDGED = 1


def build_body(producer_id: str, spool_records: list[dict]) -> bytes:
    """Spool records (one dict each, contexts inline) -> latticelog.jsonl document with context refs."""
    lines = [dumps({"type": "header", "format": "latticelog.jsonl", "version": 1, "store_id": producer_id})]
    context_refs: dict[str, int] = {}

    def context_ref(ctx):
        if ctx is None:
            return 0
        key = dumps(ctx)
        if key in context_refs:
            return context_refs[key]
        parent = context_ref(ctx.get("parent"))
        ref = len(context_refs) + 1
        context_refs[key] = ref
        line = {"type": "context", "ref": ref}
        if parent:
            line["parent"] = parent
        line["kind"] = ctx.get("kind", "Custom")
        if ctx.get("name") is not None:
            line["name"] = ctx["name"]
        line["attributes"] = ctx.get("attributes", [])
        lines.append(dumps(line))
        return ref

    for r in spool_records:
        ref = context_ref(r.get("ctx"))
        rec = {k: v for k, v in r.items() if k != "ctx"}
        if ref:
            rec["context"] = ref
        lines.append(dumps(rec))
    return ("\n".join(lines) + "\n").encode("ascii")


def envelope(producer_id: str, first: int, count: int, body: bytes, gap_acknowledged: bool = False) -> bytes:
    payload = gzip.compress(body, compresslevel=1)
    header = bytearray(ENVELOPE_HEADER)
    header[0:4] = b"LLIB"
    header[4] = 1
    header[5] = ENCODING_JSONL
    header[6] = 1  # gzip
    header[7] = FLAG_GAP_ACKNOWLEDGED if gap_acknowledged else 0
    header[8:24] = bytes.fromhex(producer_id)
    struct.pack_into("<QQII", header, 24, first, first + count - 1, count, zlib.crc32(payload) & 0xFFFFFFFF)
    return bytes(header) + payload
