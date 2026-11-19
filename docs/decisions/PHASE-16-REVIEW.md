# PHASE-16-REVIEW — Remote ingest protocol and collector

- Status: **CONDITIONAL** (protocol, client and collector work and are tested on loopback with injected faults;
  multi-producer load measured in `reports/ingest-v1.md` §3; TLS tested end to end; a real network between machines
  is still untested)
- Date: 2026-11-18
- Decisions: `docs/decisions/ADR-0009.md` · Specification: `specs/ingest-protocol.md`
- Deliverables: `src/LatticeLog.Ingest` (envelope, client, forwarder), `src/LatticeLog.Server` (collector),
  `src/LatticeLog.Collector` (the `latticelog-collector` executable, since P17 `src/LatticeLog.ServerHost` →
  `latticelog-server`), `tests/LatticeLog.Tests/IngestTests.cs`, `reports/ingest-v1.md`

| task | status | evidence |
|---|---|---|
| T01 transport | done | HTTP/1.1+2, one POST per batch; gRPC rejected with reasons (ADR-0009) |
| T02 envelope | done | 48 B header: producer, sequence range, record count, encoding, compression, CRC-32 |
| T03 semantics | done | at-least-once on the wire, effective exactly-once per producer; limits documented |
| T04 idempotency | done and tested | deduplication by (producer, local sequence); overlap trimming; explicit gaps |
| T05 ACK | done | the level actually reached (fsync/os) with group commit; duplicates also force the requested flush; replica ACK: N/A |
| T06 retries | done | exponential backoff with full jitter and `Retry-After`; non-retryable errors reported |
| T07 spool | done | the local store is the spool (durable, with a quota) |
| T08 full spool | documented | local quota: write rejected and counted (`DroppedWriteError`), never deleted; no end-to-end test |
| T09 negotiation | done | `body_encoding` field + 415 with the accepted list; binary body (2) with automatic fallback to JSONL (1), tested |
| T10 TLS/auth/rotation | done | hashed tokens and hot rotation tested; TLS with a real certificate tested end to end (`TlsTests`: forwarder and reads over HTTPS, untrusted client and plain HTTP refused; container smoke test over HTTPS); plain HTTP on a public address refused |
| T11 quotas/backpressure | done | per-token 429 tested; queue-full 503 implemented but not tested; minimum rate limit (slowloris) |
| T12 huge events/bombs | done and tested | limits on request, decompressed size, ratio, records and bytes per line |
| T13 network tests | partial | request and response loss, delays, collector crash; no TCP-level loss or reordering (one batch in flight) |
| T14 measurement | done (loopback) | ~180–217k records/s with JSONL, p50 ACK 12–15 ms per 5,000-record batch; binary body 2.0–3.4× faster |
| T15 limits of the guarantees | done | `specs/ingest-protocol.md` §4 |

- Gate G16 ("disconnect, retry and recover without silent duplicates or false confirmations"): **met on loopback with
  injected faults**, and over HTTPS with a real certificate. To be repeated over a real network between machines.
- Residual risks:
  - a collector that fails to write stops accepting until restarted (a deliberate safety decision);
  - replication is asynchronous (P19).
- Decision: proceed to P17, keeping the open items.

## Update 2026-11-18 (second part)

- **Binary body** (`body_encoding = 2`, a mini segment with a catalog copy + Lattice-S): ingestion 2.0–3.4× faster than
  JSONL on 4 CPUs (`reports/ingest-v1.md` §5). Decoding and provenance happen on the request thread, not on the writer.
  415 negotiation with fallback to JSONL, tested.
- **Incremental forwarder** (`StoreTail`): no longer re-reads the active segment on every poll. A bug found along the
  way (a retry after a failed send declared as a gap) was fixed before release and has a test.
- Still open: a real network between machines. TLS with a real certificate is now tested (`TlsTests`, container smoke test).
