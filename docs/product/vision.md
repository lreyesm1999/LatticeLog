# LatticeLog — Vision (P00)

## Problem (P00-T01)
Logs are produced with free-text APIs, stored as text or redundant JSON and read by dumping thousands of lines. Three
costs keep repeating:
1. **Capture**: formatting strings and serializing JSON on the application thread adds latency and allocations.
2. **Storage**: 80–95 % of every line is repeated (field names, service, templates, trace ids).
3. **Reading**: people and external AI get unstructured dumps, with no stable references and no idea of what was left
   out.

## Users and scenarios
| user | concrete scenario |
|---|---|
| Local developer | Instruments a .NET API with typed events and runs `latticelog query ./logs --where "level >= ERROR"` with no server. |
| Microservices team | Correlates a request across services by `trace_id`/`operation_id` and exports a Context Pack for an incident. |
| Operations / support | Checks integrity after a power cut (`verify`, `repair`) and gets error groups with exact counts. |
| Shared platform | Multi-tenant ingestion, retention, and read-only access for external agents through MCP. |

## Three differentiators (P00-T02)
1. **Efficient capture**: typed events generated at compile time (no `string.Format`, JSON or reflection on the hot
   path).
2. **Specialized storage**: an open `.llog` format with schema/context catalogs, structural codecs and per-segment
   indexes; exact reconstruction, verified.
3. **Better reading for external AI without internal AI**: deterministic evidence with coverage, stable references and
   untrusted-text marking.

## Explicit exclusions (P00-T03)
- LatticeLog **runs no AI**: no LLMs, embeddings or neural classification.
- No silent loss: every loss (filter, sampling, saturation, write error, retention) is counted.
- No promise of universal compression or superiority without published datasets and hardware.
- No mandatory cloud dependency: everything works locally.
- Outside the core: a full metrics/tracing APM, a general-purpose relational database, inferring causality from
  timestamps.

## Product surfaces (P00-T04)
SDKs (.NET, Python, PHP) · `.llog` format · `latticelog` CLI · query language · evidence API · read-only MCP adapter ·
server (P16–P24) · web console (P23).

## Status on 2026-11-18
Local engine, single-node server, replication, web console and the .NET, Python and PHP SDKs implemented as
**0.1.0-alpha.1**, with tests. Cluster and storage tiers not started. See `ROADMAP.md` and
`docs/decisions/PHASE-*-REVIEW.md`.
