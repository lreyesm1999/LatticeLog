# Show HN draft

Status: **draft, not posted.** Post only after the pre-launch checklist in [`README.md`](README.md) is green, from the
maintainer's own account, and stay in the thread for the first few hours to answer questions.

## Title

Pick one (both ≤ 80 characters):

- `Show HN: LatticeLog – typed, local-first logs in an open binary format for .NET`
- `Show HN: LatticeLog – local log store with indexed queries and MCP access for agents`

URL field: `https://github.com/lreyesm1999/LatticeLog`

## Body

> Hi HN. LatticeLog is a log and event engine I've been building for .NET 10, with Python and PHP SDKs. It is MIT
> licensed and in alpha.
>
> **The problem.** Most application logs end up as text or JSON lines. Types are lost (`42`, `"42"` and `42.0` all
> become text), the same keys are repeated millions of times, and answering "what failed in the last hour" means
> decompressing and parsing everything. When you hand that to a teammate or to a coding agent, you hand over
> copy-pasted fragments with no way to tell whether something was left out.
>
> **The approach.**
>
> - Events are declared once with an attribute, and a Roslyn source generator writes the logging method. A `long`
>   stays a `long`. On the producer side this measures 56 ns and 5 B allocated per enabled event, and ~1 ns / 0 B when
>   the level is off (null sink, one thread, BenchmarkDotNet, one machine: `reports/benchmarks-hotpath.md`).
> - Records go to `.llog`, an append-only segment format with a key/template catalog, values grouped by field, a CRC
>   per block, crash recovery, and a footer index (time, severity, event, service, bloom filter for trace ids). The
>   spec is in the repo and an independent Python reader verifies frozen golden files in CI.
> - Queries use the footer to skip segments and, since format 0.3, individual blocks. Every answer carries a
>   `coverage` section (matched, returned, truncated, partial, exhaustive yes/no) and `llog://` references back to the
>   exact records.
> - An MCP server exposes read-only tools (`list_sources`, `search_events`, `get_error_groups`, `get_trace`,
>   `get_context_pack`, …) so a coding agent can ask about a service **by name** instead of by file path. Log text in
>   the output is marked as untrusted data. LatticeLog itself runs no model.
> - Optionally, a single-node server ingests over HTTP with `(producer, sequence)` deduplication, so retries are
>   idempotent, and the local store acts as a durable outbox when the network is down.
>
> **Try it in about five minutes** (needs the .NET 10 SDK; packages are not on NuGet yet):
>
> ```
> git clone https://github.com/lreyesm1999/LatticeLog && cd LatticeLog
> dotnet run -c Release --project examples/dotnet-console -- demo-store 2000
> dotnet run -c Release --project src/LatticeLog.Cli -- errors demo-store --top 3
> ```
>
> That writes ~8,000 records from a simulated order service with an injected stock-timeout incident, then groups the
> failures deterministically and tells you whether the answer was exhaustive. Prebuilt Native AOT executables for
> Windows x64 and Linux x64 are on the Releases page with SHA256SUMS.
>
> **What it is not.** It is not a hosted observability platform, not a cluster (single node plus an async replica),
> and not production-hardened: there is no encryption at rest and no external security review yet. All benchmark
> numbers come from one Windows machine whose CPU is unstable under all-core load, so concurrency runs were pinned to
> 4 cores. The reports in `reports/` include where it loses: zstd-19 beats `.llog` on size on every dataset, and
> storing raw text lines byte-exact is 83 % larger than the same text with zstd-3.
>
> **Feedback I'm looking for:** whether the typed-event model fits how you log today, whether the coverage/evidence
> output is useful to you or your tools, and anything in the format spec that looks wrong or underspecified.

## Prepared answers to likely questions

Keep answers short, link to the file that backs the claim, and concede points that are fair.

**Why not Serilog / NLog?**
They are mature logging libraries with large sink ecosystems, and LatticeLog does not replace them for most teams.
LatticeLog owns the storage and query side too: a typed on-disk format, indexes and evidence output. You can keep
`ILogger` and add LatticeLog as one more provider (`AddLatticeLog`), so it can run next to an existing setup.

**Why not Seq / Loki / Elasticsearch / an OpenTelemetry backend?**
Those are servers you run or buy, built for central search across many machines. LatticeLog starts local: an
application writes to a folder with no infrastructure, and the CLI or MCP server reads it there. The optional server is
single-node. If you already have a central backend, LatticeLog can export OTLP/JSON and lossless `latticelog.jsonl`;
it is not trying to be a cluster.

**Why a custom binary format instead of Parquet / SQLite / JSONL + zstd?**
The format is append-only with per-block CRCs: after a crash every valid block is recovered and the torn segment is
sealed (how much not-yet-flushed data is at risk depends on the durability mode you pick). The footer index lets reads
skip what cannot match. JSONL + zstd has no index and has to be decompressed and parsed to filter. In
our measurements a full `.llog` scan was 4–9× faster than JSONL+zstd decode + parse + filter, and index pruning gave
another 20–40× on time/severity windows (`reports/baseline-v1.md` §3). It is documented
(`specs/llog-format-v0.md`) and has a second, independent reader to keep us honest. Parquet was not benchmarked.

**Why are typed events .NET-only?**
The typed declaration relies on a Roslyn source generator. The Python and PHP SDKs write typed values (Int64, Decimal,
UUID, timestamps, maps), but there is no schema-declared event in those languages yet; the event name is a field.

**Is it production ready?**
No. It is `0.1.0-alpha`. The local-v1.0 gate (P14 in `ROADMAP.md`) is marked NOT PASSED: latency on another disk and
on Linux, signed packages and a release candidate are still open. There are 232 automated tests, including
real-process crash tests and end-to-end TLS, running on Windows, Linux x64/ARM64 and macOS ARM64.

**What does MCP actually add over grep?**
Lookup by service name instead of path, deterministic error grouping, trace timelines and context packs within a byte
budget, and an explicit statement of coverage so an agent can tell "nothing found" from "not looked at". The output
marks which fields contain log text, so a downstream model can treat it as data rather than instructions.

**Is there AI inside?**
No. LatticeLog structures, filters, groups and retrieves. It performs no inference and calls no model. Interpreting
the result is up to the reader.

**How does compression compare?**
On 14 Loghub-2.0 datasets the typed model was 25 % smaller in total than the same content as CSV + zstd-3, but 17 %
larger than CSV + zstd-19 (`reports/loghub2-v1.md`). Those runs start from Loghub's own templates, which is a
best case for templating.

**Who is behind it?**
One maintainer so far. Say so plainly if asked.
