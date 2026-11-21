# Compatibility and versioning (P14-T10, P27)

## What is guaranteed

| surface | rule | how it is checked |
|---|---|---|
| `.llog` format | A reader reads the current minor version and **every earlier minor of the same major** (today 0.1, 0.2 and 0.3). A new minor only adds optional elements: an older reader ignores them or reads the segment sequentially, and never returns wrong data. | frozen test files `tests/GoldenFiles/v0.1`, `v0.2` and `v0.3`, read by the .NET reader and by the independent Python reader on every test-suite and CI run |
| `latticelog.jsonl` v1 exchange | Stable output; optional fields may be added, the meaning of an existing one never changes | round-trip tests (`EvidenceAndInteropTests`, server export) |
| Ingest protocol v1 | `body_encoding` 1 (JSONL) is always accepted; new encodings are negotiated (415 → the client falls back to an accepted one) | `IngestTests.Forwarder_falls_back_to_jsonl_when_the_server_rejects_the_binary_body`, deduplication rules with both encodings |
| HTTP API v1 (`/v1/...`) | Added fields are compatible; removing or changing one requires `/v2`. `error` codes are stable. | `ServerApiTests` |
| Cursors | Opaque. Server cursors expire (1 h) and are signed; CLI cursors are bound to the store and the query. No client should interpret them. | tampering, borrowing and expiry tests |
| Public .NET API | 0.x: may change between minors, always noted in `CHANGELOG.md`. From 1.0, strict semver. | — |

## User-facing changes in this cycle (not released yet)

- `latticelog-collector` becomes `latticelog-server`. Each namespace's store lives in `data/ns/<namespace>`. A store
  created by the P16 collector is migrated by moving it to `data/ns/default`; the collector was never released.
- `QueueCapacity` maximum is 16,777,216 (previously 100 M) and is rounded up to a power of two.
- The "friendly" JSON adds `observed_time` when present.
- The `Forwarder` sends the binary body by default; servers that do not know it trigger the automatic fallback to
  JSONL.
- New v1 API surfaces (additive, compatible): `/v1/admin/*`, `/metrics`, `/ui`. Stores of a replicated namespace keep
  `upstream` in `INGEST-CHECKPOINT.json` (an optional field; earlier versions ignore it).

## Deprecation policy

1. Anything to be removed is flagged in the CHANGELOG and the documentation at least **one minor version earlier**.
2. Meanwhile the previous version keeps working: readers, encodings and `/v1` coexist.
3. Persistent data formats (`.llog`, MANIFEST, STORE.json) are **never** made unreadable within the same major
   version. A new major ships a migration tool.
