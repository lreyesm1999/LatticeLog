# `.llog` binary format — version 0.2 (draft, normative)

Status: **draft v0.2** (0.1 files remain readable; see §12 for the changes). Breaking changes are allowed until 1.0 but every change must bump `format_minor` (compatible)
or `format_major` (incompatible) and ship new golden files (`tests/GoldenFiles/`). This document is sufficient to
write an independent reader; `tools/pyllog/llog.py` is such a reader written only from this text (gate G4).

Language: normative specs are written in English for external implementers; product documentation is in Spanish.

## 1. Conventions

- All fixed-width integers are **little-endian**.
- `varint` = unsigned LEB128, at most 10 bytes, value < 2^64. A 10th byte greater than 1 is an error.
- `zigzag(i64)` = `(n << 1) ^ (n >> 63)`; decoding `(u >> 1) ^ -(u & 1)`. `svarint` = varint of zigzag.
- `string` = `varint byte_length` + UTF-8 bytes. Invalid UTF-8 is an error (no replacement).
- `crc32` = CRC-32/ISO-HDLC (IEEE 802.3, reflected polynomial 0xEDB88320, init 0xFFFFFFFF, xorout 0xFFFFFFFF), identical to zlib `crc32`.
- References (`schema_ref`, `context_ref`) are **1-based and local to the segment**; 0 means "none".
- Readers MUST reject any count or length larger than the remaining bytes of the enclosing payload.

## 2. Segment file layout

```
FileHeader (40 bytes)
Block*                      ; framed blocks, append-only
[Footer block + Trailer]    ; present only in sealed segments
```

A segment is self-contained: every schema and context referenced by a records block is defined by an earlier
Schema/Context block of the same file.

### 2.1 File header (40 bytes)

| offset | size | field | value |
|---:|---:|---|---|
| 0 | 8 | magic | `4C 4C 4F 47 0D 0A 1A 0A` ("LLOG\r\n\x1a\n") |
| 8 | 2 | format_major | 0 |
| 10 | 2 | format_minor | 2 (writers of this version); readers accept 1 and 2 |
| 12 | 4 | required_features | 0 in v0.1. A reader MUST refuse a file with unknown required bits. |
| 16 | 4 | optional_features | bit 0 = footer carries a catalog copy (§7.3); unknown bits are ignored |
| 20 | 8 | segment_id | u64 |
| 28 | 8 | created_unix_nanos | i64 |
| 36 | 4 | header_crc | crc32 of bytes 0..35 |

A reader MUST refuse an unknown `format_major`. A newer `format_minor` is readable if no unknown required feature is set.

### 2.2 Block frame (24-byte header + payload)

| offset | size | field |
|---:|---:|---|
| 0 | 1 | block_type: 1 Schema, 2 Context, 3 Records, 15 Footer |
| 1 | 1 | codec: 0 Raw, 1 Structural (Lattice-S), 2 Contextual (Lattice-C). Schema/Context/Footer blocks use 0. |

Compression (byte 2) may be used on Records, Schema and Context blocks; Footer blocks are never compressed.
| 2 | 1 | compression: 0 none, 1 raw DEFLATE (RFC 1951, no zlib header) |
| 3 | 1 | reserved, MUST be 0 |
| 4 | 4 | payload_length (stored bytes) |
| 8 | 4 | decoded_length (bytes after decompression) |
| 12 | 4 | record_count (records in a Records block; entries in Schema/Context blocks may differ, see §5) |
| 16 | 4 | payload_crc = crc32(stored payload) |
| 20 | 4 | frame_crc = crc32(bytes 0..19 of this header) |

Limits (anti corruption / anti decompression bomb): `payload_length ≤ 64 MiB`, `decoded_length ≤ 64 MiB`,
`record_count ≤ 1,000,000`, without compression `decoded_length == payload_length`, with compression
`decoded_length ≤ payload_length × 1100 + 64`. Decompression MUST produce exactly `decoded_length` bytes.

### 2.3 Trailer (last 20 bytes of a sealed segment)

| offset | size | field |
|---:|---:|---|
| 0 | 8 | footer_offset (absolute offset of the Footer block frame) |
| 8 | 4 | flags (0) |
| 12 | 4 | magic `4C 46 4C 54` |
| 16 | 4 | crc32 of bytes 0..15 |

The Footer frame MUST end exactly where the trailer starts. A file whose trailer or footer is invalid is treated as
**unsealed**: readers scan blocks sequentially from offset 40 to end of file.

## 3. Reading and integrity rules

1. A frame with a bad `frame_crc` or invalid fields is corrupt. Readers MAY resynchronize by searching forward for
   an offset whose 24 bytes form a valid frame header and whose payload CRC matches; skipped bytes MUST be reported.
2. A frame with valid header but bad `payload_crc` is skipped using `payload_length`; its `record_count` is reported as lost.
3. A frame extending past the end of file (unsealed segment) is a **torn tail**: never decode it; report it.
4. Records decoded from a block are only returned if the whole block decodes without error ("no partial blocks").
5. CRC protects against accidental corruption only; it is not authentication.
6. **Catalog source.** For a sealed segment whose footer contains a catalog copy (§7.3), records MUST be decoded
   with that copy. Catalog blocks are still decoded and compared with the copy (every entry of a block must equal
   the copy entry with the same reference); a difference is reported. A corrupt catalog block is reported but no
   record is lost. Without a copy (format 0.1, unsealed segments, or an unreadable footer) the catalog is built from
   the Schema/Context blocks in file order, and a corrupt catalog block makes dependent records undecodable.

## 4. Values

Tagged encoding `tag:u8` + payload:

| tag | kind | payload |
|---:|---|---|
| 0 | null | — |
| 1 | bool false | — |
| 2 | bool true | — |
| 3 | int64 | svarint |
| 4 | uint64 | varint |
| 5 | float64 | 8 bytes IEEE-754 LE (bit pattern preserved: NaN payloads, −0.0) |
| 6 | string | string |
| 7 | bytes | varint length + bytes |
| 8 | array | varint count + tagged values |
| 9 | map | varint count + (key, tagged value) pairs; order and duplicate keys preserved |
| 10 | timestamp | svarint Unix nanoseconds (UTC) |
| 11 | duration | svarint nanoseconds |
| 12 | uuid | 16 bytes, RFC 4122 network (big-endian) order |
| 13 | decimal | 4 × u32: lo, mid, hi (96-bit unsigned mantissa), flags (bits 16–23 scale ≤ 28, bit 31 sign, others 0) |
| 14 | string (UTF-16) | varint code-unit count + u16 LE units; used only for strings with unpaired surrogates |
| 15 | string ref | varint 0-based index into the block dictionary (Structural/Contextual codecs only) |

Map keys use the codec's *text* encoding (§6). Maximum nesting depth: 32.

**Untagged form** (only in shaped attributes, §6.3): when a value's kind equals the schema field kind, the tag byte
is omitted for kinds int64, uint64, float64, bytes, array, map, timestamp, duration, uuid, decimal. For **bool and
string the tag is always written** (it carries information).

## 5. Schema and Context blocks (codec 0, payload is a sequence of entries)

Schema payload: `varint n` then n entries, each assigned the next `schema_ref` (1, 2, … continuing across blocks):

```
varint event_id; string name; varint version (≤ 65535); u8 severity (0..24);
u8 presence (bit0 template, bit1 description); [string template]; [string description];
varint field_count; field_count × ( string name; u8 kind (ValueKind numbers: 0 null,1 bool,3 int64,4 uint64,5 float64,
  6 string,7 bytes,8 array,9 map,10 timestamp,11 duration,12 uuid,13 decimal); u8 sensitivity (0 none,1 personal,2 secret);
  u8 has_description; [string description] )
```

Context payload: `varint n` then n entries, each assigned the next `context_ref`:

```
varint parent_ref (0 = root; MUST be < own ref); u8 kind (0 resource,1 scope,2 operation,3 request,4 custom);
u8 has_name; [string name]; varint attr_count; attr_count × (string key; tagged value)
```

Context entries are immutable snapshots; a record references only its innermost snapshot. Lookup of an inherited
attribute walks from the snapshot to the root, innermost value wins. Maximum depth 64.

## 6. Records blocks

### 6.1 Presence bits (varint, per record)

| bit | field | encoding when present |
|---:|---|---|
| 0 | observed_time | svarint unix ns |
| 1 | monotonic_time | svarint ns (producer-local) |
| 2 | schema_ref | varint |
| 3 | event_id (no schema) | varint, non-zero |
| 4 | event_name (no schema) | text |
| 5 | body | tagged value |
| 6 | trace_id | 16 bytes (Contextual: see §6.4) |
| 7 | span_id | u64 LE (non-zero) |
| 8 | trace_flags | u8 |
| 9 | operation_id | text |
| 10 | parent_operation_id | text |
| 11 | correlation_id | text |
| 12 | exception | exception (§6.5) |
| 13 | context_ref | varint |
| 14 | record_flags | u8 (bit0 redacted, bit1 raw-import, bit2 lossy-import) |
| 15 | attrs_shaped | Structural/Contextual only, see §6.3 |
| 16 | ctx_inherit | Contextual only, see §6.4 |

Unknown bits ⇒ error. Field order on the wire: `presence, sequence, timestamp, severity(u8 ≤ 24)`, then the present
fields in ascending bit order (bits 0–14), then attributes.

### 6.2 Codec 0 — Raw

`sequence` = varint (absolute), `timestamp` = svarint (absolute). *Text* = `string`. Attributes:
`varint count` + count × (string key, tagged value). No dictionary, bits 15–16 MUST be 0.

### 6.3 Codec 1 — Structural (Lattice-S)

Payload starts with the **block dictionary**: `varint n` + n `string` (no duplicates, ≤ 65535 entries).
*Text* = `varint t`: 0 ⇒ a `string` literal follows; t > 0 ⇒ `dict[t−1]`. String values may use tag 15.
`sequence` = svarint delta from the previous record's sequence (0 before the first), `timestamp` = svarint delta
from the previous record's timestamp (0 before the first).

Shaped attributes (bit 15): allowed only when the record has a schema with 1..63 fields and the attributes have
exactly the schema's field names in order. Encoding: `varint mismatch_bitmap` (bit i set ⇒ value i is tagged because
its kind differs from the field kind) then, for each field, the value untagged (bit clear) or tagged (bit set). Field
names come from the schema. A set bit whose value has the field's kind, or a clear bit whose decoded kind differs,
is an error. Without bit 15: `varint count` + count × (text key, tagged value).

### 6.4 Codec 2 — Contextual (Lattice-C)

Structural plus:
- **Trace table**: when bit 6 is set, `varint k`: 0 ⇒ 16 literal bytes follow and are appended to the block's trace
  table; k > 0 ⇒ `table[k−1]`.
- **Context inheritance** (bit 16): the record has the same trace_id, span_id, trace_flags, operation_id,
  parent_operation_id, correlation_id and context_ref as the previous record of the block. Bits 6–11 and 13 MUST be 0
  and those fields are copied from the previous record. Invalid on the first record.

### 6.5 Exception

```
text type; u8 presence (bit0 message, bit1 stack); [tagged string value message]; [tagged string value stack];
varint inner_count; inner_count × exception          ; max depth 8
```

## 7. Footer block (block_type 15, codec 0)

```
varint record_count; svarint min_time; svarint max_time; varint first_seq; varint last_seq;
varint n_events; n × (varint event_id; string event_name; varint count)
varint n_sev; n × (u8 severity; varint count)
varint n_services; n × string                       ; values of service.name in record contexts
varint n_blocks; n × (varint offset; varint total_length; u8 block_type; varint record_count;
                      svarint min_time; svarint max_time; varint first_seq; varint last_seq)
varint bloom_bits (0 = absent); [u8 hash_count; bloom_bits/8 bytes]
varint catalog_section_length (0 = absent)              ; format 0.2, see §7.3; absent in 0.1 footers
[catalog_section]
```

### 7.1 Block directory and random access

The directory lists every block (including Schema/Context). To decode records block *k* in isolation a reader uses the
footer catalog copy (0.2) or decodes the Schema/Context blocks with smaller offsets first (0.1).

### 7.2 Bloom filter

Keys: `"t:" + lowercase hex trace_id`, `"o:" + operation_id`, `"c:" + correlation_id` (UTF-8).
`h = FNV-1a-64(key)` (offset 0xcbf29ce484222325, prime 0x100000001b3); `h1 = low 32 bits`, `h2 = (high 32 bits) | 1`;
bit *i* (0 ≤ i < hash_count) = `(h1 + i·h2) mod 2^32 mod bloom_bits`; bit *b* is `byte[b >> 3] & (1 << (b & 7))`.

### 7.3 Catalog copy (format 0.2)

`catalog_section := u8 compression (0 none, 1 raw DEFLATE); varint decoded_length; payload` where the decoded
payload is the **complete catalog of the segment**: a schema entry list (§5, `varint n` + entries) followed by a
context entry list (§5), with the same 1-based numbering as the Schema/Context blocks. Limits of §2.2 apply
(`decoded_length ≤ 64 MiB`, expansion ratio ≤ 1100). A 0.1 reader sees trailing bytes in the footer, treats the
segment as unsealed and still decodes every record by scanning (compatible degradation).

## 8. Logical model mapping

Severity numbers follow the OpenTelemetry Logs Data Model (1–24). Sequence numbers are assigned by the store writer,
strictly increasing across the whole store; `llog://<store_id>/e/<sequence>` is the stable event reference
(survives compaction because records keep their sequence).

## 9. Store directory

```
STORE.json            {"format":"latticelog.store","version":1,"store_id":"<32 hex>","created":"<RFC3339>"}
MANIFEST.jsonl        journal; each line "<crc32 8 hex> <json>"; torn/corrupt lines are ignored and reported
LOCK                  held exclusively by the single writer process
segments/seg-<id:10 digits>.llog
recovered/            torn tails and quarantined files (never deleted automatically)
```

Manifest operations: `segment_sealed`, `segment_recovered`, `segment_quarantined`, `compaction_started`
(`inputs`, `output`), `compaction_committed`, `compaction_aborted`, `segment_deleted`. Live segments are all
`segments/*.llog` minus inputs of committed compactions, outputs of uncommitted/aborted compactions and deleted ids.
Readers order live segments by `first_seq` (unsealed segments last).

## 10. Hot vs cold, conversion, migration

- *Hot* segments are written by the logger (default codec Structural, no compression, small blocks) and may be unsealed.
- *Cold* segments are produced by compaction (codec/compression chosen by the planner). Conversion is exact: the
  compactor decodes, re-encodes, re-decodes and compares every record before committing.
- Migration to a future format version uses the same protocol (`compaction_*` journal entries) and keeps inputs until
  commit; rollback = keep reading the inputs (they are only deleted after commit).

## 11. Hexadecimal example

File header of `tests/GoldenFiles/v0.2/structural.llog` (segment 1, created_unix_nanos 0):

```
offset 0   4C 4C 4F 47 0D 0A 1A 0A   magic "LLOG


"
offset 8   00 00                     format_major = 0
offset 10  02 00                     format_minor = 2
offset 12  00 00 00 00               required_features = 0
offset 16  01 00 00 00               optional_features = 1 (footer catalog copy)
offset 20  01 00 00 00 00 00 00 00   segment_id = 1
offset 28  00 00 00 00 00 00 00 00   created_unix_nanos = 0
offset 36  87 5B B8 FF               crc32(bytes 0..35) = 0xFFB85B87
```

Complete vectors: `tests/GoldenFiles/v0.1/` (frozen) and `tests/GoldenFiles/v0.2/` (current): one file per codec ×
compression, `expected.llog.jsonl` (decoded records in the lossless exchange format) and two corrupt files that readers
must report. `tools/pyllog/llog.py verify-golden tests/GoldenFiles/v0.1 tests/GoldenFiles/v0.2` checks all of them.

## 12. Changes from 0.1 to 0.2

- Footer catalog copy (§7.3, optional feature bit 0) — fixes risk P04-R1: in 0.1 a single corrupt Schema/Context
  block made every records block that referenced it undecodable.
- Schema/Context blocks may be DEFLATE-compressed when the segment uses compression (allowed by the 0.1 frame
  format already, not used by 0.1 writers).
- Reading rule §3.6. No change to values, records, codecs or the trailer.
