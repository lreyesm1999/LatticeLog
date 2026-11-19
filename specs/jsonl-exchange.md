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

Local mode: the Python SDK's `latticelog.open(folder, ...)` writes these documents directly, one file per process
folder and rotation, each starting with its own header and context lines; records carry a dense `seq` per process
folder. See [local-sources.md](local-sources.md). `JsonlExchangeReader.ReadLine` parses one line with the definitions
seen so far, so an incremental reader can resume after an offset (re-feeding the saved header/context lines) and keep a
malformed line as a text record instead of losing the rest of the file.
