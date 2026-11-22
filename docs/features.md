# Features in detail

> The README keeps a short version. This page lists what each part of LatticeLog does today; the status of each area is
> in [`ROADMAP.md`](../ROADMAP.md).

## Capture: SDKs

| | .NET | Python | Rust | Node.js / TypeScript | PHP |
|---|---|---|---|---|---|
| Typed events | Source-generated (`[LatticeEvent]`, diagnostics LL0001–LL0007) | Typed values (`int` → Int64/UInt64, `Decimal`, `UUID`, `datetime`…) | Typed values (`Value` trait: integers, floats, `Bytes`, `Timestamp`, `Uuid`, `Decimal`, maps, raw UTF-16) | Typed values (safe integers → Int64, other numbers → Double, `bigint` → Int64/UInt64/Decimal, `Date`, bytes, `Map`, wrappers for UUID/decimal/duration) | Typed values |
| Integration | `ILogger` bridge, `Activity` / trace context | `logging.Handler`, structlog | `tracing` layer (spans → contexts), `log` bridge, own builder and macros | Contexts through `AsyncLocalStorage`, pino destination (typed) and transport, winston transport | Scopes and operations |
| Storage | Writes `.llog` directly | Local folder or durable JSONL spool | Local folder (writer thread, batched, allocation-free hot path) | Local folder (the call only encodes; batched positional writes and fsync on libuv's thread pool) | Spool shared across PHP processes (`flock`) |
| Shipping | `Forwarder` (binary body, falls back to JSONL) | Background thread, backoff + jitter | Background thread ships the folder's own lines byte for byte | Event-loop shipper sends the folder's own lines byte for byte | At request end, after `fastcgi_finish_request` |

See the [Python](../sdks/python), [Rust](../sdks/rust), [TypeScript](../sdks/typescript) and [PHP](../sdks/php) READMEs for setup.


## Storage: the `.llog` format

- Append-only **segments** of **blocks**; a segment is sealed when it grows and is immutable afterwards.
- **Lattice-S** codec: keys, names and templates go to a catalog; values are grouped by field, then compressed.
- **Integrity**: CRC per block; after a power cut, everything valid is recovered and the torn segment is sealed.
- **Open**: normative spec in [`specs/llog-format-v0.md`](../specs/llog-format-v0.md) and an [independent Python reader](../tools/pyllog) that verifies frozen golden files on every CI run.
- Online **compaction**, **retention** by age or size, verified **hot backup**.


## Query and evidence

```bash
latticelog query   ./logs --where "level >= ERROR AND service = 'orders-api'" --since 1h --explain
latticelog trace   ./logs --trace-id 4bf92f3577b34da6a3ce929d0e0e4736 --format markdown
latticelog stats   ./logs --by event --bucket 1m
latticelog errors  ./logs --since 24h --top 10
latticelog context ./logs --where "level >= WARN" --budget-bytes 32000 --format json
latticelog export  ./logs --format llog-jsonl --output logs.jsonl
latticelog verify  ./logs                       # exit code 3 when integrity issues are found
latticelog mcp     ./logs                       # read-only MCP server over stdio, bound to one store
latticelog mcp                                  # MCP server over every source, found by name
latticelog sources                              # list sources (Python folders are indexed first)
latticelog roots   add ./services               # per-user folders searched for sources
latticelog migrate ./old/app.log --preview      # what a migration would keep, nothing written
latticelog migrate "./old/app.log*" --name legacy-app [--follow]
latticelog mcp install --client claude-desktop  # merge LatticeLog into an MCP client's config
```

- A small [query language](../specs/query-language.md) with fields, attributes (`attr.name`), time ranges and traces. `--explain` shows which indexes pruned the scan.
- **Context Packs**: error groups, traces and samples within a byte budget, each with an `llog://` reference to the original record.
- **Untrusted-content marking**: evidence lists which fields contain text copied from logs, so downstream tools treat it as data.
- Run `latticelog --help` for every command, including `inspect`, `repair`, `compact`, `backup`, `retention` and `import`.


## Server: `latticelog-server`

| Area | What you get |
|---|---|
| Multi-tenant | Namespaces `tenant/project/env`, each with an isolated store |
| Access | Hashed bearer tokens with `ingest` / `read` / `admin` scopes per namespace; hot reload; brute-force brake |
| Read API | Search with HMAC-signed, expiring cursors · events · traces · stats · context packs · lossless export · catalog ([spec](../specs/server-api.md)) |
| Online admin | Retention, compaction, verification, privacy erasure, hot backup, all without stopping ingestion and all audited |
| Operations | Prometheus `/metrics`, audit log, web console at `/ui` (strict CSP, log text rendered as text only) |
| Replication | Asynchronous primary → replica, with manual failover and no duplicates |
| Ingestion | Binary or JSONL bodies, group commit, quotas, bounded queues, TLS required off loopback |

```bash
dotnet run --project src/LatticeLog.ServerHost -- hash-token "<secret>"   # put the hash in tokens.json
dotnet run --project src/LatticeLog.ServerHost -- --data ./server-data --tokens tokens.json
# → http://127.0.0.1:7400/ui  ·  /v1/search  ·  /metrics
```

See the [single-node deployment guide](deploy/single-node.md), the [operations runbook](operations/runbook.md) and the [replication guide](operations/replication.md).


## Repository layout

```text
src/
  LatticeLog.Abstractions         event model and public API
  LatticeLog.Core                 logger: bounded queue, durability modes, scopes, redaction
  LatticeLog.Generator            Roslyn source generator for typed events
  LatticeLog.Storage              .llog format, codecs, writer/reader, recovery, compaction, retention, backup
  LatticeLog.Query                query language, index-aware planner, cursors, aggregations
  LatticeLog.Evidence             context packs and evidence documents
  LatticeLog.Ingest               remote ingestion protocol, client and Forwarder
  LatticeLog.Server               latticelog-server library (API, admin, replication, console)
  LatticeLog.ServerHost           latticelog-server executable
  LatticeLog.Cli                  latticelog CLI (also a dotnet tool, trimming/AOT-ready)
  LatticeLog.Sources              sources by name: roots, latticelog.toml, Python index, migration
  LatticeLog.Mcp                  MCP server (read tools over sources, opt-in migration)
  LatticeLog.Service              per-user background service, MCP shim, registration (task/systemd/launchd)
  LatticeLog.OpenTelemetry        OTLP/JSON and importers
  LatticeLog.Extensions.Logging   Microsoft.Extensions.Logging bridge
sdks/python  sdks/rust  sdks/typescript  sdks/php   SDKs for other languages
specs/                           normative specs (format, ingest protocol, server API, query language)
examples/dotnet-console          demo workload with an injected incident
examples/developer-flow          structlog service + migrated legacy log, asked over MCP by name
tools/pyllog                     independent Python reader of the format
deploy/                          server Dockerfile and its HTTPS smoke test
tests/  benchmarks/  research/  reports/  scripts/
```
