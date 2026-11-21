# Changelog

## Unreleased — public repository under the MIT license

- **Web console redesign** (`llog ui`):
  - **Theme:** follows the system, with a header button that switches between System, Light and Dark (remembered
    per browser). The dark theme is deep graphite and the light one crisp white, both with an indigo accent.
  - **Layout:** a translucent header, segmented tabs and a status pill. Cards have more depth. Stat cards get
    accent lines and larger numbers.
  - **Charts:** histogram and level bars use gradients.
  - **Lists:** level pills carry a status dot. Source icons sit on tinted tiles.
  - **Polish:**
    - shimmering placeholder rows while a search runs;
    - a blurred backdrop behind `Ctrl+K`;
    - thin scrollbars;
    - short entrance animations, turned off when the system asks for reduced motion.
  - **Phone width:** the tabs and level filters scroll sideways instead of widening the page.

- **Faster web console and time histograms.**
  - **Time histograms** (`/v1/stats?bucket=`, `latticelog stats --bucket`) now count every block whose records all
    match and fall into one bucket straight from the segment's footer directory, without decoding it. This works
    with no filter, with time bounds and with `level >= X`. Over 1.76 M events, the whole-source histogram the
    console draws on every search went from about 510 ms to 4 ms with daily bars, and to 23 ms with 5-minute bars.
    The counts are unchanged.
  - **Bucket keys** are formatted once per bucket instead of once per record.
  - **Source explorer:** the list only puts the visible rows in the page (virtual scrolling). Per-source search
    text, folder and newest-event time are computed once, and sorting and numbers use shared `Intl` formatters.
    With 3,000 sources, a full redraw went from about 215 ms to 9 ms, and re-sorting from 184 ms to 7 ms.
  - **Overview** draws each count as it arrives, instead of waiting for the slowest (the per-service count reads
    every event).
  - **Counts are reused for 20 s** when moving between views. Search and Refresh always count again.

- **Source explorer in the web console** (`llog ui`): the source drop-down is replaced by a Sources view laid out like
  a file explorer. A folder tree (registered roots and their subfolders, latticelog.toml projects, migrated logs by the
  original file's folder, plus All, Pinned and Recent), a sortable list with events, size, newest event and an
  active/idle/no-data marker, a breadcrumb, a search across every folder, and a details pane (events over time, level
  mix, path, retention, last indexing error) with Open logs, Errors and Overview. `Ctrl+K` jumps to a source by name
  from anywhere. With several sources and none chosen, the console starts in the explorer. New service route
  `GET /v1/sources` (`?refresh=1` rescans the roots).

- **`llog update`** installs the newest GitHub release: it downloads the executable for the platform and `SHA256SUMS`,
  refuses a hash mismatch, and runs the new version's own `setup`. Data, config and MCP entries are kept. `--check`
  only reports; `--version` and `--prerelease` pick a release. New workflow `release.yml` publishes a release (Native
  AOT executables for Windows x64 and Linux x64, plus `SHA256SUMS`) when a `v*` tag is pushed. A hyphenated tag
  becomes a pre-release. `latticelog --version` now reports the version the executable was built as.
- **Fixed (found on a real Windows install):**
  - `roots`/`allow` changes made from inside a packaged app (such as Claude Desktop) never reached the service. Windows
    redirected the write into the app's private copy of AppData. Config commands now send the change to the running
    service, which writes the file it reads (new line-protocol operation `config`), and warn when they write from
    inside a package.
  - `setup` failed with "access denied" while an editor was running `latticelog.exe mcp`. It now moves the running
    executable aside and cleans the old copy up later.
  - With `LATTICELOG_CONFIG_DIR`/`LATTICELOG_DATA_DIR` or `--config-dir` set, the MCP shim woke the user's registered
    service and waited about 15 s for it. It now starts its own.
- **Install once: background service and single-file install** ([`specs/local-sources.md` §6](specs/local-sources.md#6-background-service),
  new library `LatticeLog.Service`, README "Install once"):
  - `latticelog service run|start|stop|restart|status|install|uninstall|ui`: one per-user process on 127.0.0.1
    (token-protected, endpoint file in the data folder) that owns the catalogs. It does background refresh and Python
    indexing, follows `follow=true` migrations, and compacts and applies per-source retention
    (`latticelog sources retention`) to LatticeLog's own stores only. All of its writes share one gate, so concurrent
    editors no longer contend for index locks. Registration is per user, with no administrator rights: a Task
    Scheduler logon task running a console-less copy (`latticelogw.exe`) or the Run key, a systemd user unit, or a
    launchd agent.
  - `latticelog mcp` is now a thin stdio shim to the service, which it starts when it is not running. It falls back to
    in-process when the service cannot start (`--no-service` forces it). It survives a service restart by
    reconnecting, replaying `initialize` and resending in-flight requests. Tools and results are unchanged;
    `list_sources` adds `served_by`.
  - `latticelog service status` shows sources, last indexed time, disk use and errors. `/ui` (the web console) and
    `/v1/status` are served on loopback, with Host-header checks against DNS rebinding.
  - `latticelog setup`: copies the Native AOT single-file executable to a per-user folder and onto `PATH`, registers
    and starts the service, and runs `mcp install` for the chosen clients. `latticelog setup --uninstall` undoes it,
    and `mcp uninstall` removes one entry. Scripts: `scripts/publish-aot.ps1`, `scripts/install.ps1`,
    `scripts/install.sh`, `scripts/smoke-aot.ps1|.sh`.
  - Short command `llog` (installed by `setup` next to the executable) and `llog ui`, which opens the web console in
    the browser; one-letter flags `-o`, `-h`, `-y`.
  - CI builds the AOT executable for win-x64 and linux-x64 and smoke-tests list_sources and get_error_groups with
    dotnet removed from PATH, then again in containers without .NET (Windows Server Core, Debian).
  - `StoreWriter.ApplyRetention(..., keepNewest)` and `SourceCatalog` thread safety (snapshot warnings, serialized
    discovery) support the service.

- **Developer flow: logs by service name** ([`specs/local-sources.md`](specs/local-sources.md), new library
  `LatticeLog.Sources`, example [`examples/developer-flow`](examples/developer-flow)):
  - Python **local mode** without a server: `latticelog.open(folder, service=..., environment=...)` writes official
    `latticelog.jsonl` v1 records next to a `latticelog.source.json` manifest, one `p-N` subfolder per live process
    (shown as one source), dense sequence across restarts, durability modes, retention by age/size that never deletes
    the file being written.
  - `latticelog.structlog_processor(log)`: one line in `structlog.configure`; `event` → event name, other keys typed
    attributes, exceptions with their real Python type. `LatticeLogHandler` keeps structlog's event dict under
    `ProcessorFormatter.wrap_for_formatter` instead of flattening it with `getMessage()`.
  - Discovery by name: `latticelog roots add|remove|list`, optional project `latticelog.toml` (`roots`, `[sources]`,
    `[migrate] allow`), `latticelog sources`. Python folders get an incremental `.llog` index (checkpoints per file,
    complete lines only, crash-safe, no duplicates); .NET stores are read directly.
  - MCP: `latticelog mcp` without a store serves every source; new `list_sources`; every tool takes `source` (name,
    list or `*`), results merged by time and tagged with their source; composite cursors page across sources.
    `latticelog mcp <dir>` keeps the single-store, read-only server.
  - Migration on request: `preview_migration` (read-only: format, line count, 5 samples as stored, field mapping,
    estimated size, loss report) and `migrate_logs` (`readOnlyHint: false`, `destructiveHint: false`): only inside the
    registered roots or the allowlist (`latticelog allow add`), originals never modified, staged then registered by one
    rename (a cancelled run leaves nothing), idempotent with rotation/truncation detection, unparsed lines kept as
    text, compaction at the end, progress and cancellation over MCP. CLI parity: `latticelog migrate <path|glob>
    --name <source> [--follow] | --preview`. A copy of a real 378 MB structlog file: 1,762,702 records, 7.9 MB, 7 s.
  - `latticelog mcp install --client claude-desktop|claude-code|cursor|vscode` merges the client configuration, asks
    before overwriting an entry and keeps a backup.
  - `JsonlExchangeReader.ReadLine` parses one line at a time (resumable reads; a malformed line can be kept as text).
- **Format `.llog` 0.3: block summaries.** The footer now records, for each Records block, the severities, events
  and services it contains (`specs/llog-format-v0.md` §7.4), so `level >= ERROR`, `event = '…'` and `service = '…'`
  skip individual blocks instead of only whole segments. The footer gains a tagged extension area that readers skip
  when they don't know a tag, so later additions won't break 0.3 readers. 0.1/0.2 segments stay readable and gain
  summaries when compacted or re-imported; 0.2 readers fall back to a full scan of 0.3 segments, and an older store
  writer must not open a 0.3 store (it keeps every record but moves the 0.3 footers to `recovered/` and rewrites
  them). New golden files in `tests/GoldenFiles/v0.3/`, and `pyllog` recomputes the summaries from the decoded
  records. On a real 1.76M-record structlog store (1 segment, 431 Records blocks), `level >= ERROR AND event = '…'`
  now decodes 4,096 records in ~12 ms to the first hit, down from 1,687,552 records in ~500 ms; the footer grows by
  2.2 KB.

- **MIT** license (`LICENSE`): the project is free and open, with no commercial editions. Package metadata
  (`PackageLicenseExpression`, repository) in `Directory.Build.props`, `pyproject.toml` and `composer.json`.
- Clean-up before publishing: zero build warnings (`CancellationToken` ordering, `Dispose` of the wrapper stream, the
  LL0007 diagnostic message) and a `.gitignore` that no longer hides `sdks/php/bin/`.
- Two timing-sensitive tests made deterministic; whitespace formatting enforced in CI; community files, issue forms,
  PR template, CODEOWNERS and Dependabot.
- All documentation translated to English.
- **TLS tested end to end** (`TlsTests`, on Linux, macOS and Windows): forwarder and read API over HTTPS with a real
  certificate; an untrusted client and plain HTTP are refused.
- **Docker image built, scanned and smoke-tested in CI** (`container` job, `deploy/smoke-test.sh`): Trivy scan, health
  over HTTPS, ingestion with the Python SDK, queries, non-root user, data kept across a restart. The Dockerfile now
  gives `/data` to the non-root user (a fresh volume was not writable) and ignores `global.json` in the image build.
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
- **CI on ARM64** (Linux `ubuntu-24.04-arm`, in addition to macOS ARM64) and **CodeQL** static analysis (C#, Python,
  JavaScript and workflows) on every PR, on `main` and weekly.
- **Backup restore drill on every CI run**: a backup taken on Linux x64 is restored on Windows x64, macOS ARM64 and
  Linux ARM64, verified, compared record by record with the source and written to.
- New tests: a full writer queue answers 503 with `Retry-After` and the refused batches are stored exactly once on
  resend; a full producer spool refuses and counts new records, deletes nothing and still ships what it stored.
- Test stability: the replication test waits for the replica's deduplication cursor, which is published after the
  shared flush, instead of its last sequence.
- Test stability: the token quota test runs with a frozen quota clock (an internal `ServerOptions.QuotaClock`) and the
  bucket arithmetic has its own fake-clock test; the crash test waits for the child's first acknowledgement before its
  random kill delay instead of assuming the process starts within 250 ms.
- **Fixed (found importing a real structlog log)**: JSONL import used structlog's `event` key as the event name even
  when it held free text, so every log sentence became its own event. `event` is now the event name only when it looks
  like an identifier; free text becomes the body.
- **Fixed**: Python tracebacks imported with the exception type "Traceback (most recent call last)". The type and
  message now come from the line after the last frame (`httpx.HTTPStatusError: …`).
- **Fixed**: `overview` reported at most 10,000 `error_events` and `errors` at most 10,000 `matched_events` (the
  grouping candidate limit). Both totals are exact now, and the documents say how many events the groups were built
  from. Error groups without a body show the exception message, and Markdown shows each group's event and exception
  type.
- `query --explain` states that severity, event, service and key filters skip whole segments only.

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
