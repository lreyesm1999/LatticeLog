# How LatticeLog compares

LatticeLog sits in several categories at once (a logging library, a local storage format and query engine, an MCP
server and an optional single-node server), so a straight "X vs Y" is rarely fair. This page compares capabilities,
not quality, and says when a tool is simply in a different category.

Facts about other projects were checked on 2026-11-21 against their public documentation. They change; corrections
are welcome as issues or pull requests.

## Summary

| | LatticeLog | Serilog | NLog | Seq | OpenTelemetry .NET | Grafana Loki |
|---|---|---|---|---|---|---|
| Category | Library + local store + query engine | Logging library | Logging library | Centralized log server with UI | Telemetry SDK and wire protocol | Log aggregation backend |
| Where logs end up | `.llog` files in a folder (optionally shipped to `latticelog-server`) | Any sink: files, consoles, servers | Any target: files, consoles, servers | Seq's server | Any OTLP backend | Loki (object storage + index) |
| Typed attributes kept in storage | Yes (Int64, Decimal, UUID, timestamps, bytes, maps…) | Depends on the sink | Depends on the target | Structured JSON events | Typed on the wire; storage depends on the backend | Labels are indexed; line content is text |
| Compile-time generated log methods | `[LatticeEvent]` generator | Not built in; MEL's `[LoggerMessage]` works through `ILogger` | Same as Serilog | n/a | n/a | n/a |
| Query engine included | Yes, local (CLI, MCP, console, HTTP) | No | No | Yes | No | Yes (LogQL) |
| Needs a server | No | No | No | Yes | No, but a backend to store | Yes |
| Open storage format with an independent reader | Yes ([spec](../specs/llog-format-v0.md), [reader](../tools/pyllog)) | Text/JSON sinks are open formats | Same | No | n/a | Server-internal |
| Coding-agent access (MCP) | Built in, read-only, coverage-aware | Not built in | Not built in | Community MCP servers (not from Datalust) | Not built in | Through Grafana's MCP server |
| Clustering / scale-out | No | n/a | n/a | Vendor documentation | n/a | Yes |
| Maturity | Alpha, single maintainer | Mature | Mature | Mature commercial product | Stable, CNCF | Mature, CNCF ecosystem |
| License | MIT | Apache-2.0 | BSD-3-Clause | Commercial (free tier) | Apache-2.0 | AGPL-3.0 |

## When to use which

- **You only need to write logs somewhere** and already have a backend: Serilog or NLog through `ILogger` are mature
  and have hundreds of sinks. LatticeLog can be added as one more `ILogger` provider without replacing them.
- **A team needs a shared, searchable log UI today:** Seq or a Loki/Grafana stack. LatticeLog's server is single-node
  and alpha.
- **You standardize on OpenTelemetry:** keep it. LatticeLog imports OTLP/JSON and exports to it, so a local store can
  sit next to an OTel pipeline.
- **LatticeLog fits** when logs must stay on the machine (desktop, edge, on-prem, a developer laptop), when you want
  typed events with no server at all, when you need to prove what a query covered, or when you want a coding agent to
  read logs by service name without shipping them anywhere.

## Things LatticeLog does not do

- No metrics or distributed tracing backend (it stores trace ids with log records and can show a trace's log timeline).
- No alerting.
- No cluster, sharding or object-storage tier.
- No AI: it structures, filters and retrieves; interpretation is up to the reader.
