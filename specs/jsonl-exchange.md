# Lossless JSON Lines exchange `latticelog.jsonl` v1 (P13-T10)

Purpose: leave LatticeLog without the binary format and without loss. `decode(encode(x)) == x` is tested
(`QueryTests.Lossless_jsonl_exchange_round_trips`) and the lab checks it by SHA-256 on every workload.

Lines: a `{"type":"header","format":"latticelog.jsonl","version":1,"store_id":...}` line first; then `schema` and
`context` definition lines before first use (`ref` numbering local to the file); then `record` lines.

Typed values:
- JSON string, `true`, `false`, `null`.
- int64: JSON number without `.` or exponent. float64: JSON number that always contains `.` or an exponent (`1.0`, `-0.0`).
- Wrappers: `{"$u64":"..."}`, `{"$f64bits":"<16 hex>"}` (NaN, payload kept), `{"$f64":"Infinity"}`,
  `{"$bytes":"<base64>"}`, `{"$ts":"<RFC 3339, 9 digits>"}`, `{"$dur_ns":"..."}`, `{"$uuid":"..."}`,
  `{"$dec":"..."}` (scale preserved), `{"$utf16le":"<base64>"}` (strings with unpaired surrogates),
  `{"$map":[[k,v],...]}` (ordered, duplicates allowed).
- Attributes are `[[key, value], ...]`. Times are RFC 3339 with 9 fractional digits, UTC.
