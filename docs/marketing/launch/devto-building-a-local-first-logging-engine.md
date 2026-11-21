---
title: Building a Local-First Logging Engine for .NET
published: false
description: How LatticeLog captures typed events, stores them in an open binary format, and answers queries with an explicit statement of what it covered. Including what didn't work.
tags: dotnet, csharp, logging, opensource
cover_image:
canonical_url:
---

<!--
Status: draft, not published. Every number below links to a report in the repository. Before publishing, re-check the
numbers against reports/ (they change when benchmarks are rerun) and replace relative paths with full GitHub URLs:
https://github.com/lreyesm1999/LatticeLog/blob/main/<path>
-->

Most application logs end their lives as lines of text or JSON. That works until you need to answer a specific
question: which orders failed in the last hour, and why? Then you `grep`, decompress, parse, and hope nothing important
was in the file you skipped. If you paste the result to a teammate or a coding agent, neither of you can tell whether
the excerpt is complete.

I've been building [LatticeLog](https://github.com/lreyesm1999/LatticeLog), an MIT-licensed log and event engine for
.NET 10 (with Python and PHP SDKs), to see how far a different set of defaults can go:

- events keep their **types** from the call site to the disk;
- the storage format is **open and documented**, with a second reader written independently;
- queries **skip** what they cannot match, and every answer **states what it covered**;
- everything works **locally**, with no server, and a server is optional.

This post walks through the architecture, the decisions behind it, the measurements, and the experiments that failed.
It is alpha software, and I'll be explicit about what isn't done.

## What "local-first" means here

The application writes to a folder on its own disk. Nothing has to be running for logging to work: no collector, no
database, no network. Reading happens through a CLI, an MCP server for AI tools, or a web console, all against the same
files.

If you do configure a server, the local store doubles as a durable outbox: records wait on disk while the network or the
server is down, and a forwarder ships them later. The application never waits on the network to log.

LatticeLog is **not** a hosted observability platform and not a cluster. The server is single-node, with an
asynchronous replica and manual failover. If you need central search across hundreds of machines today, this isn't it.

## Capture: typed events and a source generator

An event is declared once:

```csharp
static partial class AppEvents
{
    [LatticeEvent(Id = 2101, Name = "inventory.stock.timeout", Level = Severity.Error,
                  Message = "Stock query for {productId} timed out after {elapsedMs} ms")]
    public static partial void StockTimeout(ILatticeLogger logger, long productId, int elapsedMs);
}
```

A Roslyn source generator writes the method body at compile time. There is no reflection and no string formatting on
the call. `productId` is stored as a 64-bit integer and `elapsedMs` as a 32-bit one, so a query like
`attr.productId = 42` is an exact typed comparison, not a string match. The generator reports mistakes as compiler
diagnostics (LL0001–LL0007), for example a template placeholder with no matching parameter.

The value model covers integers of each width, doubles (NaN and infinities included), `decimal` with its scale, UUIDs,
timestamps, durations, bytes, arrays and ordered maps. Parameters can be marked `[Sensitive]`, and redaction happens
before anything is persisted.

Operations and scopes are first-class: `logger.BeginOperation("PlaceOrder", "order-991")` creates an immutable context
that every record inside it references, and W3C trace context is picked up from `Activity.Current`.

If you already use `ILogger`, there's a provider:

```csharp
builder.Logging.AddLatticeLog(LatticeLogger.CreateLocal("./logs"));
```

That path is slower: Microsoft.Extensions.Logging formats the message and allocates its state object before the
provider sees it. Use generated events on hot paths.

### The hot path, and why the numbers got worse before they got better

The producer hands records to a writer thread through a queue. The first version used a `ConcurrentQueue` plus a sorted
set of tickets. Measured with BenchmarkDotNet against a null sink (producer, queue and writer thread, but no disk), one
producer thread:

| step | typed event (4 fields) | allocated |
|---|---:|---:|
| `ConcurrentQueue` + `SortedSet` of tickets | 189 ns | 417 B |
| preallocated bounded ring (position = ticket) | 80 ns | 488 B |
| redaction without enumerators | 75 ns | 360 B |
| record pool | **56 ns** | **5 B** |

The bounded ring more than halved the time but *raised* the allocations. Before the ring, two thirds of the records
were dropped by saturation and never reached the writer thread, so the writer's allocations weren't being counted.
With the ring, 0 of 193 M records were dropped, and the real cost showed up. That led straight to the next fix: the
redaction step iterated an `IReadOnlyList` with `foreach`, allocating an enumerator per attribute per record.

The record pool only recycles a record when the sink declares that it doesn't retain records. Any other sink leaves
records to the GC, and caller-supplied attribute arrays are never reused.

With the level disabled, the call costs about 1.1 ns and allocates nothing. That is slower than a disabled
`LoggerMessage` call in MEL (~0.1 ns) because of an interface dispatch on `IsEnabled`.

Every drop is counted. When the queue is full, the default policy blocks the producer up to a timeout (after which the
record is dropped and counted); you can choose to drop the newest record immediately or to throw instead.

Source: [`reports/benchmarks-hotpath.md`](../../../reports/benchmarks-hotpath.md). Latency with a real disk at fixed
rates, with coordinated omission accounted for: [`reports/latency-v1.md`](../../../reports/latency-v1.md).

### Durability you can check

There are three modes: `Fast` (buffered), `Balanced` and `Durable` (fsync). `FlushAsync` returns the level that was
**actually** reached, not the one requested:

```csharp
var ack = await logger.FlushAsync(AckLevel.FsyncConfirmed);
Console.WriteLine(ack.Level);
```

The point is that a caller can never be told "durable" when the data isn't.

## Storage: the `.llog` format

A store is a folder of **segments**. A segment is an append-only file of **blocks**; once it grows past its size limit
it is sealed and never modified again.

- **Block frame.** A 24-byte header with type, codec, lengths and a CRC, then the payload. A block whose payload fails
  its CRC is skipped and its record count reported as lost; records are never returned from a partially decoded block.
- **Catalog.** Keys, event names and message templates are written once to schema and context blocks and referenced by
  number afterwards. Millions of repetitions of `"orderId"` become a small integer.
- **Lattice-S codec.** Inside a records block, values are grouped by field and then compressed with DEFLATE. DEFLATE is
  skipped automatically when it doesn't shrink the block.
- **Footer.** A sealed segment ends with a footer: time range, sequence range, counts per event and severity, the
  services present, a directory of every block with its time and sequence range, a bloom filter over trace, operation
  and correlation ids, and a copy of the catalog so any block can be decoded on its own.
- **Block summaries (format 0.3).** The footer can also carry per-block summaries, so severity, event and service
  filters skip individual blocks, not only whole segments.
- **Recovery.** After a power cut, every block that verifies is kept and the torn segment is sealed. `latticelog
  verify` exits with code 3 when it finds integrity problems.

The format is specified in [`specs/llog-format-v0.md`](../../../specs/llog-format-v0.md). To keep the spec honest
there's a second reader, [`tools/pyllog`](../../../tools/pyllog), written in Python with no shared code. On every CI
run it has to decode frozen golden files for each format version, including a bit-flipped and a truncated file, and
produce the expected records. Golden files are never edited: a format change adds a new set.

That second reader paid off in format 0.2. With 0.1, a single flipped bit in a schema block made all 27 records in
the golden test unreadable. Adding the catalog copy to the footer brought that to 0 of 27.

Data isn't locked in: `latticelog export` writes lossless `latticelog.jsonl` or OTLP/JSON.

## Query: skip first, then decode

The query language is small: fields, typed attributes (`attr.productId = 42`), time ranges and trace ids.

```bash
latticelog query ./logs --where "level >= ERROR AND service = 'orders-api'" --since 1h --explain
```

The planner reads footers first. A segment whose time range, severity counts or service list can't match is skipped
without being opened. Inside a segment, block ranges and summaries skip blocks. `--explain` prints which indexes pruned
the scan. Some questions don't need any block decoded at all:

```text
$ latticelog stats demo-store --by event
order.received            2000
inventory.stock.reserved  1992
...
-- total 8037 (from footer indexes, no block decoded)
```

On synthetic workloads of 100,000 records, a full `.llog` scan was 4–9× faster than decompressing, parsing and
filtering the same data stored as JSONL + zstd, and time/severity pruning was a further 20–40× faster than the full
scan ([`reports/baseline-v1.md`](../../../reports/baseline-v1.md) §3). The trace-id bloom filter prunes segments, but
a matching block is still decoded; there's no per-block trace index.

## Evidence: answers that say what they covered

The part I care about most is what comes back. Every evidence document (`errors`, `trace`, `context`, `event`,
`overview`) has a `coverage` section:

```json
"coverage": { "matched_events": 16, "returned_events": 2, "truncated": false,
              "partial_results": false, "sampling": "none", "exhaustive": "yes" }
```

`exhaustive` is `yes` only when nothing was omitted and the scan was complete. A consumer must not present a truncated
or partial document as complete.

Every record in the output has a stable reference like `llog://c17423fc7296431a89fd8881fb45b0cc/e/196`, which resolves
to the original record with `latticelog event` (or the MCP tool `get_event`). Claims can be checked.

Error groups are deterministic fingerprints, explicitly labelled "not root causes". Context packs select records in a
fixed priority order (errors first, at most three per fingerprint, then scope boundaries, warnings, neighbours sharing
a trace or operation, then even samples) until a byte budget is used, and the same request against the same store
produces a byte-identical document.

Each document also lists which fields contain text copied from logs, with a note to treat them as data, not
instructions. Log lines are attacker-influenced text, and that matters once a language model reads them.

## MCP: logs by service name for external tools

LatticeLog runs no AI and performs no inference. What it does provide is a read-only
[Model Context Protocol](https://modelcontextprotocol.io/) server, so coding agents and AI clients can ask about a
service by **name**:

- `list_sources`, `search_events`, `get_event`, `get_trace`, `get_related_events`, `get_error_groups`,
  `get_context_pack`, `get_event_schema`, `get_overview`;
- `preview_migration` and `migrate_logs`, which convert traditional text or JSON logs into a source only when asked,
  never touching the originals.

```bash
latticelog roots add ./logs                    # once: services under this folder are found by name
latticelog mcp install --client claude-code    # or claude-desktop, cursor, vscode
```

`mcp install` backs up the client's config and asks before replacing an existing entry. A per-user background service
listens on `127.0.0.1` only and serves every MCP client from one process, so several editors don't fight over an
index. If it can't start, the MCP shim serves in-process.

I haven't run a structured evaluation with external models yet, so I'm not claiming it makes agents better at
debugging. The design goal is narrower: an agent should be able to tell "there were no errors" from "I didn't look".

## Remote: idempotent by construction

For central collection there's `latticelog-server`: namespaces (`tenant/project/env`) with isolated stores, hashed
bearer tokens scoped to `ingest`, `read` or `admin`, TLS required off loopback, a read API with signed expiring cursors,
online retention/compaction/backup, an audit log and Prometheus metrics.

Every record carries a `(producer, sequence)` pair. The server deduplicates on it and rebuilds that state from its own
data after a crash, so a retry is always safe, and an ACK reports the durability actually reached. The forwarder tails
the local store and ships binary bodies (a mini segment with catalog and Lattice-S); with 4 producers on 4 pinned CPUs
the server ingested about 1.15 M records/s, 2–3.4× the JSONL body
([`reports/ingest-v1.md`](../../../reports/ingest-v1.md) §5).

## Results, and where LatticeLog loses

All numbers come from one machine: an i9-14900KF on Windows 11, .NET 10. That CPU is unstable under all-core load
(likely the known Raptor Lake microcode issue), so concurrency runs were pinned to 4 cores. They are not a sizing guide.

**Size, on public data.** On 14 Loghub-2.0 datasets, events stored typed (template as the event, wildcard values as
typed attributes) were **25 % smaller** in total than the same content as CSV + zstd-3, smaller on all 14. Against
CSV + zstd-19, a slow offline compressor, they were **17 % larger** in total and won on only 4 of 14. These runs start
from Loghub's own annotated templates, which is the best case
([`reports/loghub2-v1.md`](../../../reports/loghub2-v1.md)).

**Size, on synthetic workloads.** Lattice-S + DEFLATE was smaller than JSONL + zstd-3 on 7 of 8 workloads (−1.4 % to
−23.4 %) and larger on adversarial random data (+9.2 %). zstd-19 was smaller on every workload, at 9–16 seconds per
100,000 records versus about 0.1 s ([`reports/baseline-v1.md`](../../../reports/baseline-v1.md)).

**Negative results I kept in the reports:**

- **Raw text compresses badly.** Importing raw log lines byte-exact, so they can be exported identically, costs 83 %
  more than the same text under zstd-3. Line bodies are opaque to the codec and DEFLATE's 32 KiB window can't compete.
  That import is for keeping the original and making it queryable, not for saving space.
- **No predictive codec.** Before building "Lattice-P", I computed an optimistic bound with adaptive code lengths
  (previous-event context for event identity, second-order deltas for timestamps). The best case was 0.6 % of the
  compressed size, so it wasn't built.
- **Context inheritance is marginal.** "Lattice-C" saves 3–21 % before general compression but only 0–3.5 % after
  DEFLATE. It's an option, not the default.
- **The target wasn't met.** I set out for 30 % below the best applicable baseline and didn't get there.

Every report has a reproduction command; `scripts/reproduce.sh` reruns the tests, the baseline and the latency
benchmark.

## Decisions and trade-offs

- **A custom format instead of SQLite or Parquet.** I wanted append-only writes with no compaction on the write path,
  per-block integrity, crash recovery that keeps everything valid, and an index I could reason about down to the byte.
  The cost is real: a format to maintain, version and document. The independent reader and golden files are how I try
  to pay that cost honestly. I haven't benchmarked against Parquet.
- **DEFLATE instead of zstd.** It ships with .NET, with no native dependency. zstd as a block codec is the obvious next
  experiment; it needs a format version and an ADR.
- **Types at the source instead of parsing later.** The typed model only shines when events are designed. Free text and
  migrated logs work, but they don't get the size advantage.
- **Honest acknowledgements over optimistic ones.** Returning the level actually reached complicates the API, but
  "durable" should mean durable.

## Limitations

- **Alpha** (`0.1.0-alpha`). The local v1.0 gate is marked NOT PASSED in the roadmap: repeating latency on another disk
  and on Linux, signed packages and a release candidate are open.
- **No packages** on NuGet, PyPI or Packagist yet. You build from source, or use the prebuilt Native AOT executables
  for Windows x64 and Linux x64 from GitHub Releases (checksummed).
- **No encryption at rest** and no external security review.
- **No cluster**, no object-storage tier.
- **Typed events are .NET only**; Python and PHP write typed values but have no schema-declared events yet.
- **One machine** for every measurement so far.

There are 232 automated tests, including real-process crash tests, lossy networks, server restarts and end-to-end TLS,
running in CI on Windows x64, Linux x64, Linux ARM64 and macOS ARM64. CI also restores a backup on a different OS and
CPU architecture and smoke-tests the Native AOT CLI with no .NET installed.

## Try it

You need the .NET 10 SDK.

```bash
git clone https://github.com/lreyesm1999/LatticeLog
cd LatticeLog
dotnet run -c Release --project examples/dotnet-console -- demo-store 2000
dotnet run -c Release --project src/LatticeLog.Cli -- errors demo-store --top 3
```

The first command writes about 8,000 records from a simulated order service, with an injected stock-timeout incident,
into a single segment of around 760 KB. The second groups the failures:

```text
- `4971c8d6ee1067be` ×8 ERROR `order.failed` exception=`System.TimeoutException`
- `a85a07c80de55c35` ×8 ERROR `inventory.stock.timeout`
```

with a coverage table that says `exhaustive: yes`. From there, `event`, `trace` and `context` follow the `llog://`
references.

## Contributing

Issues and pull requests are welcome. The most useful things right now:

- run the benchmarks on stable hardware, especially Linux, and send the numbers;
- read the format spec and tell me where it's ambiguous;
- try the MCP server with your own agent and report where the output was unhelpful.

Start with [`CONTRIBUTING.md`](../../../CONTRIBUTING.md). Format changes need a spec update, new golden files and a
pass of the independent reader.

Thanks for reading. Criticism of the design is the most useful thing you can send.
