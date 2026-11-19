# Changelog

## Unreleased — public repository under the MIT license

- **MIT** license (`LICENSE`): the project is free and open, with no commercial editions. Package metadata
  (`PackageLicenseExpression`, repository) in `Directory.Build.props`, `pyproject.toml` and `composer.json`.
- Clean-up before publishing: zero build warnings (`CancellationToken` ordering, `Dispose` of the wrapper stream, the
  LL0007 diagnostic message) and a `.gitignore` that no longer hides `sdks/php/bin/`.
- Two timing-sensitive tests made deterministic; whitespace formatting enforced in CI; community files, issue forms,
  PR template, CODEOWNERS and Dependabot.
- All documentation translated to English.
- **Fixed (found by CI on macOS)**: a reader could open a segment that the writer had just created but not yet given
  its header, and `Aggregate` failed with `File shorter than header`. New segments are now staged as `*.llog.tmp` and
  renamed once the header is written; `Aggregate` and `GetBySequence` report an unreadable segment as a coverage issue
  like `Execute` already did.
- **Fixed**: the incremental tail reader treated a frame that was briefly unreadable during a slow fsync as corruption
  after 50 polls and fell back to a full scan. The threshold is now a time (`StoreTail.StallTimeout`, 2 s, replacing
  `StallLimit`), and `StoreTail.LastFullScanReason` says why a full scan happened.
- **Fixed**: when the tail reader moved to the next segment and a segment's index was briefly unreadable (being
  sealed), it could jump to a later sealed segment and skip a range; the gap check then forced a full scan. It now
  reads the older, still-unreadable segment first, and an internal discontinuity resets the incremental position.
- Test stability: the token quota test runs with a frozen quota clock (an internal `ServerOptions.QuotaClock`) and the
  bucket arithmetic has its own fake-clock test; the crash test waits for the child's first acknowledgement before its
  random kill delay instead of assuming the process starts within 250 ms.

## Unreleased — operations, security, replication and SDKs (P17–P24)

- **Retention** by age or size: whole sealed segments, oldest first; journaled before deletion and never touches the
  active segment. CLI `latticelog retention` and `POST /v1/admin/retention`.
- **Online administration** without stopping ingestion: compaction, verification, **privacy erasure**
  (`/v1/admin/erase`, rewrites without leaving a trace, and resends do not bring the data back) and a verified **hot
  backup** (`/v1/admin/backup`, `latticelog backup`).
- **Faster ingestion**: binary body (`body_encoding = 2`, a Lattice-S mini segment), 2.0–3.4× faster than JSONL on
  4 CPUs; decoding moved off the writer thread. Automatic fallback to JSONL when the server answers 415.
- Incremental **Forwarder** (`StoreTail`): no longer re-reads the active segment on every poll.
- **Security**: audit log (`data/audit/*.jsonl`, CRC per line) of admin actions, reads and authentication failures;
  a brake at 20 failures/min per address; `nosniff`/`no-referrer` headers; JSON 500s without internal details;
  `server.key` with mode 0600.
- **Asynchronous replication** (`--replicate-to`): one forwarder per namespace; the replica deduplicates after a
  producer failover using provenance.
- **Operations**: `GET /metrics` (Prometheus), a web console at `/ui` (strict CSP, never renders log text as HTML),
  runbooks.
- **SDKs**: Python (`logging`, durable spool, background shipping) and PHP (spool shared across processes with `flock`,
  shipped at the end of the request).
- **Queries**: `attributes.<name>` is an alias of `attr.<name>`. A segment deleted while a query runs is reported as a
  coverage issue instead of being skipped silently.
- Fixed before release: a retry of the incremental forwarder could declare a false gap and lose data.

## Unreleased — query server v1 (P17) and multi-producer load

- `latticelog-server` (replaces `latticelog-collector`):
  - **namespaces** `tenant/project/environment`, with an isolated store per namespace;
  - **tokens** with `ingest`/`read`/`admin` scopes and allowed namespaces.
- Read API (`specs/server-api.md`):
  - search with coverage and **HMAC-signed, expiring** cursors;
  - event, related, trace, stats, overview, error groups, context pack and schema;
  - paginated lossless export;
  - segment catalog, and health with details for admins.
- Read/ingest separation: query semaphore, per-token concurrency, timeout and a scanned-records cap with an explicit
  partial result.
- Arrival time (`observed_time`) set by the server and included in the JSON output.
- `deploy/Dockerfile` (chiseled, non-root; not built yet) and `docs/deploy/single-node.md`.
- Measured with 1–64 producers: group commit of up to 27 batches per fsync and a peak of ~600k records/s with 4
  producers. With producers and readers at once, they compete for CPU (`reports/ingest-v1.md` §3–§4).
- **Platform finding**: the reference machine (i9-14900KF, microcode 0x11D) corrupts memory under load on many
  cores, also without LatticeLog (`tools/platform-stress`). Concurrency measurements are taken with a limited CPU set.

## Unreleased — remote ingestion v1 (P16)

- Protocol v1 (`specs/ingest-protocol.md`, ADR-0009): HTTP `POST /v1/ingest`, a 48-byte binary envelope (producer,
  local sequence range, record count, encoding, compression, CRC-32) and a `latticelog.jsonl` body, optionally gzipped.
- `LatticeLog.Ingest`: `IngestClient` and `Forwarder`. The local store is the spool, with one cursor per endpoint.
  Retries with exponential backoff, jitter and `Retry-After`; oversized batches are split and gaps are resynchronized.
- `LatticeLog.Server` + `latticelog-collector`:
  - **deduplication** by (producer, local sequence), rebuilt from stored data after a crash;
  - **group commit** with the ACK level actually reached and an `ingest` provenance context;
  - **access**: SHA-256-hashed tokens with hot rotation, per-token quotas (429), bounded queue (503);
  - **limits** on size, decompression, ratio and line length; TLS required off loopback.
- Tests: lossy network (request and response), collector crash without a checkpoint, duplicate/overlap/gap rules,
  security and limits, rotation and quotas. Measured: ~180–217k records/s and p50 ACK of 12–15 ms per 5,000-record
  batch on loopback (`reports/ingest-v1.md`).

## Unreleased — background durability

- **Root cause of the writer-thread stalls found**: storage latency spikes (fsync up to 5.3 s, seals up to 3.8 s on an
  SSD at 98 % capacity) plus the NTFS rule that a write waits while the same file is being flushed. GC pauses are ruled
  out (≤ 6.6 ms). New instrumentation: `LatticeLoggerOptions.SlowWriterOperationThreshold`,
  `StoreWriterOptions.Diagnostics` and `latency --diag`.
- **Background durability** (`StoreWriterOptions.BackgroundDurability`, on by default): sealing rotated segments and
  the periodic Balanced fsync move to a store-owned thread (FIFO, merged requests, sticky failure).
  `StoreWriter.DurableSequence`, `RequestBackgroundFsync` and `PendingDurabilityWork` are new.
  `IRecordSink.RequestBackgroundFsync` and `IRecordSink.DurableSequence` have default implementations.
  `SegmentWriter` writes with `RandomAccess`.
- Result: 1 producer at 500k/s goes from 4/12 runs with a stall to 0/12. With 4 producers, stalls remain when the disk
  takes seconds to flush (`reports/latency-v1.md` §4).

## Unreleased — SDK hardening (P14)

- **Real crash tests**: a child process (`tests/LatticeLog.CrashHarness`) is killed mid-write 3 times per mode
  (Fast/Balanced/Durable) with segment rotation; no acknowledged record is lost, the store is left as a prefix with no
  gaps or duplicates, and recovery is clean.
- **Hot path**: the queue becomes a preallocated bounded ring (`BoundedRing`) where the position is the acknowledgement
  ticket (no `SortedSet` on the writer thread); a record pool (`LogRecordPool`) for generated events, `WriteEvent` and
  the free-text API; allocation-free redaction. Typed event: 189 → 56 ns and 417 → 5 B per event; free text:
  252 → 63 ns and 340 → 105 B (`reports/benchmarks-hotpath.md`).
- `IRecordSink.RetainsRecords` (default `true`): a sink that does not retain records allows them to be recycled.
  `StoreWriter` returns `false`.
- `QueueCapacity` is rounded up to a power of two, the ring is allocated when the logger is created (16 B per slot) and
  the maximum drops from 100 M to 16,777,216.
- `BalancedFsyncBytes` (optional, 0 = off): an extra volume-based fsync in Balanced mode.
- **Fixed-rate latency**: new `latency` mode in the benchmarks project (exact p50/p99/p99.9/max, service and response
  time relative to the schedule). Open finding: writer-thread I/O stalls of 0.1–2 s in Balanced at 500k events/s on the
  reference machine (`reports/latency-v1.md`).
- **Trimming/AOT**: the `IsAotCompatible` analysis was never active (it was evaluated before `TargetFramework` was
  known); moved to `Directory.Build.targets`, with IL trimming/AOT warnings as errors under `src/`. Fixed reflection-based
  JSON serialization in the CLI, query cursors (source generator, same JSON) and MCP. The trimmed CLI works (verify,
  inspect, query with a cursor, stats, context, MCP). Real Native AOT is verified in CI (Linux).
- Local **NuGet packages** (9 packages + symbols); `LatticeLog.Core` bundles the generator; the CLI as a `dotnet tool`
  (`LatticeLog.Cli`). Not yet published to NuGet.
- **Loghub-2.0** (CC-BY-4.0) downloaded with approval and verified by MD5; new `loghub` lab experiment
  (`reports/loghub2-v1.md`).
- Open segments: catalog risk analyzed (a crash cut never leaves orphaned records, tested); no format change (ADR-0006).

## Unreleased — `.llog` format 0.2

- **P04-R1 mitigated**: the footer of every sealed segment stores a compressed copy of the schema and context catalogs;
  a corrupt catalog block no longer loses records (it is still reported). Readers check catalog blocks against the copy.
- Catalog blocks are DEFLATE-compressed when the segment uses compression (on the `inventory` workload, with one
  context per operation, total size drops by 30 %).
- Random access to a block no longer needs to read the preceding catalog blocks.
- Compatibility: 0.1 files are still read (frozen `v0.1` golden files); a 0.1 reader treats a 0.2 segment as unsealed
  and walks it sequentially.
- Independent Python reader updated; `verify-golden` accepts several folders.

## 0.1.0-alpha.1 — 2026-11-18 (unreleased)

First version of the local engine.

- Event model (typed values with exact fidelity, immutable contexts, trace context, exceptions).
- Logger: bounded MPSC queue, writer thread with group commit, Fast/Balanced/Durable modes, verifiable ACK, saturation
  policies, counters for every loss, redaction before persistence, scopes/operations, `Activity`.
- Roslyn generator for typed events with diagnostics LL0001–LL0007 and a schema lock file.
- `.llog` format v0.1 (specified in `specs/llog-format-v0.md`), Raw, Lattice-S and Lattice-C codecs, optional DEFLATE,
  footer index with a bloom filter, frozen golden files and an independent Python reader.
- Local store: rotation, quota without automatic deletion, single-writer lock, journal, recovery of torn tails,
  quarantine, atomic and resumable compaction with record-by-record verification.
- Queries (typed DSL, index pruning, stable cursors, EXPLAIN, aggregations from indexes).
- `latticelog.evidence.v1` evidence: overview, trace, related, error groups, Context Pack with budget and coverage,
  safe Markdown.
- Interoperability: lossless exchange JSONL, OTLP/JSON (export/import with a loss report), structured JSONL
  (CLEF/pino), byte-exact text; `Microsoft.Extensions.Logging` bridge.
- `latticelog` CLI (18 commands) and a read-only MCP server over stdio.
