# LatticeLog documentation

## Start here

| | |
|---|---|
| [Getting started](guides/getting-started.md) | Build, produce logs with an incident, investigate it: five minutes |
| [Install](guides/install.md) | The single executable, the per-user service, updates and uninstall |
| [Features](features.md) | Everything LatticeLog does today, by area |
| [Comparison](comparison.md) | Serilog, NLog, Seq, OpenTelemetry, Loki: what overlaps and what does not |
| [Benchmarks](benchmarks.md) | Results, hardware, method, how to reproduce, limitations |

## Guides

| | |
|---|---|
| [ASP.NET Core and `ILogger`](guides/aspnetcore.md) | The MEL bridge, typed events, one trace per request |
| [MCP setup](guides/mcp.md) | Claude Code, Cursor, Claude Desktop, VS Code |
| [Investigating an incident with an agent](guides/investigate-with-an-agent.md) | What an agent calls and what it gets back |
| [Migrating from text logs](guides/migrating-from-text-logs.md) | Run side by side, then convert old files on request |
| [Troubleshooting](guides/troubleshooting.md) | Build, CLI, MCP and service problems |
| [Python SDK](../sdks/python) · [Rust SDK](../sdks/rust) · [PHP SDK](../sdks/php) | Other languages |

## Reference

| | |
|---|---|
| [`.llog` format](../specs/llog-format-v0.md) | Normative binary format, with an [independent reader](../tools/pyllog) |
| [Event model](../specs/event-model.md) · [context model](../specs/context-model.md) | What a record is |
| [Query language](../specs/query-language.md) | `--where` filters |
| [Evidence protocol](../specs/evidence-protocol.md) | Coverage, refs, untrusted-content marking |
| [Local sources](../specs/local-sources.md) | Sources by name, `latticelog.toml`, migration, the background service |
| [Ingest protocol](../specs/ingest-protocol.md) · [server API](../specs/server-api.md) | Remote ingestion and the HTTP API |
| [OpenTelemetry mapping](../specs/otel-mapping.md) · [JSONL exchange](../specs/jsonl-exchange.md) | Interoperability |
| [Durability](../specs/durability.md) · [ordering](../specs/ordering.md) · [schema compatibility](../specs/schema-compat.md) | Guarantees |
| [Compatibility and versioning](compatibility.md) | What a version promises |

## Operating the server

[Single-node deployment](deploy/single-node.md) · [runbook](operations/runbook.md) ·
[replication](operations/replication.md) · [threat model](security/threat-model.md)

## Project

[Roadmap](../ROADMAP.md) · [changelog](../CHANGELOG.md) · [contributing](../CONTRIBUTING.md) ·
[architecture decisions and phase reviews](decisions) · [master plan](MASTER-PLAN.md) · [product vision](product/vision.md)
