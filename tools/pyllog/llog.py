#!/usr/bin/env python3
"""Independent reference reader for the .llog v0.1 format (gate G4).

Written only from specs/llog-format-v0.md, using the Python standard library (zlib for CRC-32 and DEFLATE).
It shares no code with the .NET implementation.

Usage:
    python tools/pyllog/llog.py verify-golden tests/GoldenFiles/v0.1
    python tools/pyllog/llog.py dump <file.llog>
"""
import base64
import calendar
import decimal
import json
import struct
import sys
import zlib
from datetime import datetime

MAGIC = b"LLOG\r\n\x1a\n"
TRAILER_MAGIC = b"LFLT"
KINDS = {"Null": 0, "Bool": 1, "Int64": 3, "UInt64": 4, "Double": 5, "String": 6, "Bytes": 7, "Array": 8, "Map": 9,
         "Timestamp": 10, "Duration": 11, "Uuid": 12, "Decimal": 13}
CTX_KINDS = {"Resource": 0, "Scope": 1, "Operation": 2, "Request": 3, "Custom": 4}
SENS = {"None": 0, "Personal": 1, "Secret": 2}
UNTAGGED_SKIP = {1, 6}  # bool and string keep their tag


class FormatError(Exception):
    pass


def crc32(b):
    return zlib.crc32(b) & 0xFFFFFFFF


class Reader:
    def __init__(self, data):
        self.d = data
        self.p = 0

    def need(self, n):
        if n < 0 or self.p + n > len(self.d):
            raise FormatError("truncated at %d (need %d)" % (self.p, n))

    def u8(self):
        self.need(1)
        v = self.d[self.p]
        self.p += 1
        return v

    def raw(self, n):
        self.need(n)
        v = self.d[self.p:self.p + n]
        self.p += n
        return bytes(v)

    def u32(self):
        return struct.unpack("<I", self.raw(4))[0]

    def u64(self):
        return struct.unpack("<Q", self.raw(8))[0]

    def varint(self):
        result, shift = 0, 0
        for i in range(10):
            b = self.u8()
            if i == 9 and b > 1:
                raise FormatError("varint overflow")
            result |= (b & 0x7F) << shift
            if not b & 0x80:
                return result
            shift += 7
        raise FormatError("varint too long")

    def svarint(self):
        u = self.varint()
        return (u >> 1) ^ -(u & 1)

    def count(self, min_bytes=1):
        n = self.varint()
        if min_bytes and n > (len(self.d) - self.p) // min_bytes:
            raise FormatError("count %d exceeds remaining data" % n)
        return n

    def string(self):
        n = self.count(1)
        try:
            return self.raw(n).decode("utf-8", errors="strict")
        except UnicodeDecodeError:
            raise FormatError("invalid utf-8")

    @property
    def end(self):
        return self.p >= len(self.d)


def i64(u):
    return u - (1 << 64) if u >= 1 << 63 else u


# ---------------------------------------------------------------- values (spec §4)

def read_text(r, d):
    if d is None:
        return r.string()
    t = r.varint()
    if t == 0:
        return r.string()
    return d[t - 1]


def read_value(r, d, depth=0):
    if depth > 32:
        raise FormatError("value nesting too deep")
    return read_payload(r, r.u8(), d, depth)


def read_payload(r, tag, d, depth):
    if tag == 0:
        return ("null",)
    if tag == 1:
        return ("bool", False)
    if tag == 2:
        return ("bool", True)
    if tag == 3:
        return ("i64", r.svarint())
    if tag == 4:
        return ("u64", r.varint())
    if tag == 5:
        return ("f64bits", r.u64())
    if tag == 6:
        return ("str", r.string())
    if tag == 14:
        n = r.count(2)
        return ("str", r.raw(n * 2).decode("utf-16-le", errors="surrogatepass"))
    if tag == 15:
        if d is None:
            raise FormatError("string ref outside dictionary codec")
        return ("str", d[r.varint()])
    if tag == 7:
        return ("bytes", r.raw(r.count(1)))
    if tag == 8:
        n = r.count(1)
        return ("arr", tuple(read_value(r, d, depth + 1) for _ in range(n)))
    if tag == 9:
        n = r.count(2)
        items = []
        for _ in range(n):
            k = read_text(r, d)
            items.append((k, read_value(r, d, depth + 1)))
        return ("map", tuple(items))
    if tag == 10:
        return ("ts", r.svarint())
    if tag == 11:
        return ("dur", r.svarint())
    if tag == 12:
        return ("uuid", r.raw(16).hex())
    if tag == 13:
        lo, mid, hi, flags = r.u32(), r.u32(), r.u32(), r.u32()
        if flags & 0x7F00FFFF or (flags >> 16) & 0xFF > 28:
            raise FormatError("invalid decimal")
        return ("dec", flags >> 31, lo | (mid << 32) | (hi << 64), (flags >> 16) & 0xFF)
    raise FormatError("unknown value tag %d" % tag)


KIND_TAG = {3: 3, 4: 4, 5: 5, 7: 7, 8: 8, 9: 9, 10: 10, 11: 11, 12: 12, 13: 13}
VALUE_KIND = {"null": 0, "bool": 1, "i64": 3, "u64": 4, "f64bits": 5, "str": 6, "bytes": 7, "arr": 8, "map": 9,
              "ts": 10, "dur": 11, "uuid": 12, "dec": 13}


def read_untagged(r, kind, d):
    if kind in UNTAGGED_SKIP:
        return read_value(r, d)
    if kind not in KIND_TAG:
        raise FormatError("kind %d cannot be untagged" % kind)
    return read_payload(r, KIND_TAG[kind], d, 0)


def read_exception(r, d, depth=0):
    if depth > 8:
        raise FormatError("exception too deep")
    typ = read_text(r, d)
    p = r.u8()
    if p & ~3:
        raise FormatError("exception presence")
    msg = read_value(r, d)[1] if p & 1 else None
    stack = read_value(r, d)[1] if p & 2 else None
    n = r.count(2)
    return (typ, msg, stack, tuple(read_exception(r, d, depth + 1) for _ in range(n)))


# ---------------------------------------------------------------- catalogs (spec §5)

def decode_schemas(payload, schemas):
    r = Reader(payload)
    for _ in range(r.count(5)):
        eid = r.varint()
        name = r.string()
        version = r.varint()
        sev = r.u8()
        p = r.u8()
        tmpl = r.string() if p & 1 else None
        desc = r.string() if p & 2 else None
        fields = []
        for _ in range(r.count(4)):
            fname = r.string()
            kind = r.u8()
            sens = r.u8()
            has = r.u8()
            fields.append((fname, kind, sens, r.string() if has == 1 else None))
        schemas.append((eid, name, version, sev, tmpl, tuple(fields), desc))
    if not r.end:
        raise FormatError("trailing bytes in schema block")


def decode_contexts(payload, contexts):
    r = Reader(payload)
    for _ in range(r.count(4)):
        parent = r.varint()
        if parent > len(contexts):
            raise FormatError("context parent defined later")
        kind = r.u8()
        name = r.string() if r.u8() == 1 else None
        attrs = []
        for _ in range(r.count(2)):
            k = r.string()
            attrs.append((k, read_value(r, None)))
        parent_chain = contexts[parent - 1] if parent else ()
        contexts.append(((kind, name, tuple(attrs)),) + parent_chain)
    if not r.end:
        raise FormatError("trailing bytes in context block")


# ---------------------------------------------------------------- records (spec §6)

def decode_records(codec, payload, count, schemas, contexts):
    r = Reader(payload)
    d = None
    if codec in (1, 2):
        d = [r.string() for _ in range(r.count(1))]
    elif codec != 0:
        raise FormatError("unknown codec")
    traces = []
    prev = None
    prev_seq, prev_ts = 0, 0
    out = []
    for _ in range(count):
        p = r.varint()
        if p >> 17:
            raise FormatError("unknown presence bits")
        if codec == 0:
            seq, ts = r.varint(), r.svarint()
        else:
            seq = (prev_seq + r.svarint()) & 0xFFFFFFFFFFFFFFFF
            ts = i64((prev_ts + r.svarint()) & 0xFFFFFFFFFFFFFFFF)
            prev_seq, prev_ts = seq, ts
        rec = {"seq": seq, "ts": ts, "sev": r.u8(), "observed": 0, "mono": 0, "schema": None, "event_id": 0,
               "event_name": None, "body": None, "attrs": (), "trace": None, "span": None, "tflags": 0, "op": None,
               "pop": None, "corr": None, "exc": None, "ctx": None, "flags": 0}
        if p & 1: rec["observed"] = r.svarint()
        if p & 2: rec["mono"] = r.svarint()
        if p & 4:
            ref = r.varint()
            if not 1 <= ref <= len(schemas): raise FormatError("unknown schema ref %d" % ref)
            rec["schema"] = schemas[ref - 1]
        if p & 8: rec["event_id"] = r.varint()
        if p & 16: rec["event_name"] = read_text(r, d)
        if p & 32: rec["body"] = read_value(r, d)
        if p & 64:
            if codec == 2:
                k = r.varint()
                if k == 0:
                    traces.append(r.raw(16).hex())
                    rec["trace"] = traces[-1]
                else:
                    rec["trace"] = traces[k - 1]
            else:
                rec["trace"] = r.raw(16).hex()
        if p & 128: rec["span"] = "%016x" % r.u64()
        if p & 256: rec["tflags"] = r.u8()
        if p & 512: rec["op"] = read_text(r, d)
        if p & 1024: rec["pop"] = read_text(r, d)
        if p & 2048: rec["corr"] = read_text(r, d)
        if p & 4096: rec["exc"] = read_exception(r, d)
        if p & 8192:
            ref = r.varint()
            if not 1 <= ref <= len(contexts): raise FormatError("unknown context ref %d" % ref)
            rec["ctx"] = contexts[ref - 1]
        if p & 16384: rec["flags"] = r.u8()
        if p & 65536:
            if codec != 2 or prev is None:
                raise FormatError("invalid context inheritance")
            for k in ("trace", "span", "tflags", "op", "pop", "corr", "ctx"):
                rec[k] = prev[k]
        if p & 32768:
            fields = rec["schema"][5]
            mismatch = r.varint()
            attrs = []
            for i, f in enumerate(fields):
                v = read_value(r, d) if mismatch >> i & 1 else read_untagged(r, f[1], d)
                attrs.append((f[0], v))
            rec["attrs"] = tuple(attrs)
        else:
            attrs = []
            for _ in range(r.count(2)):
                k = read_text(r, d)
                attrs.append((k, read_value(r, d)))
            rec["attrs"] = tuple(attrs)
        out.append(rec)
        prev = rec
    if not r.end:
        raise FormatError("trailing bytes after records")
    return out


# ---------------------------------------------------------------- segment (spec §2, §3, §7)

def parse_frame(data, off):
    h = data[off:off + 24]
    if len(h) < 24 or crc32(h[:20]) != struct.unpack("<I", h[20:24])[0]:
        return None
    btype, codec, comp, res = h[0], h[1], h[2], h[3]
    plen, dlen, count, pcrc = struct.unpack("<IIII", h[4:20])
    if btype not in (1, 2, 3, 15) or codec > 2 or comp > 1 or res != 0:
        return None
    return btype, codec, comp, plen, dlen, count, pcrc


def read_segment(data):
    """Returns (records, issues, footer_dict_or_None)."""
    if data[:8] != MAGIC:
        raise FormatError("bad magic")
    if crc32(data[:36]) != struct.unpack("<I", data[36:40])[0]:
        raise FormatError("header crc")
    major, minor, required = struct.unpack("<HHI", data[8:16])
    if major != 0 or required != 0:
        raise FormatError("unsupported version/features")
    end = len(data)
    footer = None
    if len(data) >= 40 + 24 + 20 and data[-8:-4] == TRAILER_MAGIC and crc32(data[-20:-4]) == struct.unpack("<I", data[-4:])[0]:
        foff = struct.unpack("<Q", data[-20:-12])[0]
        f = parse_frame(data, foff)
        if f and f[0] == 15 and foff + 24 + f[3] == len(data) - 20 and crc32(data[foff + 24:foff + 24 + f[3]]) == f[6]:
            footer = decode_footer(data[foff + 24:foff + 24 + f[3]])
            end = foff
    schemas, contexts, records, issues = [], [], [], []
    off = 40
    while off < end:
        f = parse_frame(data, off)
        if f is None or off + 24 + f[3] > end:
            issues.append(("corrupt_or_torn", off))
            break  # this reference reader stops at the first corrupt frame (resync is optional in the spec)
        btype, codec, comp, plen, dlen, count, pcrc = f
        payload = data[off + 24:off + 24 + plen]
        off += 24 + plen
        if crc32(payload) != pcrc:
            issues.append(("payload_crc", off))
            continue
        if comp == 1:
            payload = zlib.decompress(payload, wbits=-15)
            if len(payload) != dlen:
                issues.append(("decoded_length", off))
                continue
        try:
            if btype == 1:
                decode_schemas(payload, schemas)
            elif btype == 2:
                decode_contexts(payload, contexts)
            elif btype == 3:
                records.extend(decode_records(codec, payload, count, schemas, contexts))
        except (FormatError, IndexError) as e:  # unknown references are errors (spec §2, §5): skip the whole block
            issues.append(("undecodable_block", off, str(e)))
    return records, issues, footer


def decode_footer(payload):
    r = Reader(payload)
    f = {"records": r.varint(), "min": r.svarint(), "max": r.svarint(), "first": r.varint(), "last": r.varint()}
    f["events"] = [(r.varint(), r.string(), r.varint()) for _ in range(r.count(3))]
    f["sev"] = dict((r.u8(), r.varint()) for _ in range(r.count(2)))
    f["services"] = [r.string() for _ in range(r.count(1))]
    f["blocks"] = [(r.varint(), r.varint(), r.u8(), r.varint(), r.svarint(), r.svarint(), r.varint(), r.varint()) for _ in range(r.count(8))]
    bits = r.varint()
    if bits:
        k = r.u8()
        f["bloom"] = (bits, k, r.raw(bits // 8))
    if not r.end:
        raise FormatError("trailing bytes in footer")
    return f


def fnv1a64(b):
    h = 0xcbf29ce484222325
    for x in b:
        h ^= x
        h = (h * 0x100000001b3) & 0xFFFFFFFFFFFFFFFF
    return h


def bloom_contains(bloom, key):
    bits, k, data = bloom
    h = fnv1a64(key.encode("utf-8"))
    h1, h2 = h & 0xFFFFFFFF, (h >> 32) | 1
    for i in range(k):
        b = ((h1 + i * h2) & 0xFFFFFFFF) % bits
        if not data[b >> 3] & (1 << (b & 7)):
            return False
    return True


# ---------------------------------------------------------------- expected JSONL (latticelog.jsonl v1)

def rfc3339_to_ns(s):
    main, frac = s[:-1].split(".")
    secs = calendar.timegm(datetime.strptime(main, "%Y-%m-%dT%H:%M:%S").timetuple())
    return secs * 1_000_000_000 + int(frac.ljust(9, "0"))


def json_value(v):
    if v is None:
        return ("null",)
    if isinstance(v, bool):
        return ("bool", v)
    if isinstance(v, str):
        return ("str", v)
    if isinstance(v, list):
        return ("arr", tuple(json_value(x) for x in v))
    if isinstance(v, RawNumber):
        t = v.text
        if "." in t or "e" in t or "E" in t:
            return ("f64bits", struct.unpack("<Q", struct.pack("<d", float(t)))[0])
        return ("i64", int(t))
    if isinstance(v, dict):
        if len(v) == 1:
            (k, x), = v.items()
            if k == "$u64": return ("u64", int(x))
            if k == "$f64bits": return ("f64bits", int(x, 16))
            if k == "$f64": return ("f64bits", struct.unpack("<Q", struct.pack("<d", float("inf") if x == "Infinity" else float("-inf")))[0])
            if k == "$bytes": return ("bytes", base64.b64decode(x))
            if k == "$ts": return ("ts", rfc3339_to_ns(x))
            if k == "$dur_ns": return ("dur", int(x))
            if k == "$uuid": return ("uuid", x.replace("-", ""))
            if k == "$utf16le": return ("str", base64.b64decode(x).decode("utf-16-le", errors="surrogatepass"))
            if k == "$map": return ("map", tuple((p[0], json_value(p[1])) for p in x))
            if k == "$dec":
                dd = decimal.Decimal(x)
                sign, digits, exp = dd.as_tuple()
                return ("dec", sign, int("".join(map(str, digits))) if digits else 0, max(0, -exp))
        return ("map", tuple((k, json_value(x)) for k, x in v.items()))
    raise ValueError(v)


class RawNumber:
    def __init__(self, text):
        self.text = text


def json_exc(e):
    if e is None:
        return None
    return (e["type"], e.get("message"), e.get("stack"), tuple(json_exc(i) for i in e.get("inner", [])))


def read_expected(path):
    schemas, contexts, out = {}, {}, []
    with open(path, encoding="utf-8") as f:
        for line in f:
            o = json.loads(line, parse_float=RawNumber, parse_int=RawNumber)
            t = o["type"]
            if t == "schema":
                fields = tuple((x["name"], KINDS[x["kind"]], SENS[x.get("sensitivity", "None")], x.get("description")) for x in o["fields"])
                schemas[int(o["ref"].text)] = (int(o["id"].text), o["name"], int(o["version"].text), int(o["severity_number"].text),
                                               o.get("template"), fields, o.get("description"))
            elif t == "context":
                parent = contexts[int(o["parent"].text)] if "parent" in o else ()
                attrs = tuple((p[0], json_value(p[1])) for p in o["attributes"])
                contexts[int(o["ref"].text)] = ((CTX_KINDS[o["kind"]], o.get("name"), attrs),) + parent
            elif t == "record":
                out.append({
                    "seq": int(o.get("seq", "0")), "ts": rfc3339_to_ns(o["time"]), "sev": int(o["severity_number"].text),
                    "observed": rfc3339_to_ns(o["observed_time"]) if "observed_time" in o else 0,
                    "mono": int(o.get("monotonic_ns", "0")),
                    "schema": schemas[int(o["schema"].text)] if "schema" in o else None,
                    "event_id": int(o["event_id"].text) if "event_id" in o else 0,
                    "event_name": o.get("event"),
                    "body": json_value(o["body"]) if "body" in o else None,
                    "attrs": tuple((p[0], json_value(p[1])) for p in o.get("attributes", [])),
                    "trace": o.get("trace_id"), "span": o.get("span_id"),
                    "tflags": int(o["trace_flags"].text) if "trace_flags" in o else 0,
                    "op": o.get("operation_id"), "pop": o.get("parent_operation_id"), "corr": o.get("correlation_id"),
                    "exc": json_exc(o.get("exception")),
                    "ctx": contexts[int(o["context"].text)] if "context" in o else None,
                    "flags": int(o["flags"].text) if "flags" in o else 0,
                })
    return out


def verify_golden(directory):
    import os
    expected = read_expected(os.path.join(directory, "expected.llog.jsonl"))
    ok = True
    for name in ["raw", "raw-deflate", "structural", "structural-deflate", "contextual", "contextual-deflate"]:
        data = open(os.path.join(directory, name + ".llog"), "rb").read()
        recs, issues, footer = read_segment(data)
        good = recs == expected and not issues and footer is not None and footer["records"] == len(expected)
        if footer and "bloom" in footer:
            for r in expected:
                if r["trace"] and not bloom_contains(footer["bloom"], "t:" + r["trace"]):
                    good = False
        if not good:
            ok = False
            for i, (a, b) in enumerate(zip(recs, expected)):
                if a != b:
                    diff = [k for k in a if a[k] != b[k]]
                    print("  %s: first difference at record %d in %s" % (name, i, diff))
                    break
        print("%-20s %s (%d records, %d issues)" % (name, "OK" if good else "FAIL", len(recs), len(issues)))
    for name in ["corrupt-truncated", "corrupt-bitflip"]:
        recs, issues, _ = read_segment(open(os.path.join(directory, name + ".llog"), "rb").read())
        detected = len(issues) > 0 and all(r in expected for r in recs)
        ok &= detected
        print("%-20s %s (%d valid records returned, issues: %s)" % (name, "DETECTED" if detected else "NOT DETECTED", len(recs), issues))
    return ok


if __name__ == "__main__":
    if len(sys.argv) >= 3 and sys.argv[1] == "verify-golden":
        sys.exit(0 if verify_golden(sys.argv[2]) else 1)
    if len(sys.argv) >= 3 and sys.argv[1] == "dump":
        recs, issues, footer = read_segment(open(sys.argv[2], "rb").read())
        for r in recs:
            print(r)
        print("issues:", issues, "footer records:", footer and footer["records"])
        sys.exit(0)
    print(__doc__)
    sys.exit(2)
