# LatticeLog — PRD v0.1 (P00)

Version 0.1 · 2026-11-18 · Status: draft for review. Thresholds are **targets/hypotheses**, not results.

## 1. Functional requirements (local engine, M1–M3)
| id | requirement | status |
|---|---|---|
| F1 | Typed events declared with attributes; compile-time validation of ids, names, placeholders, types and schema changes. | implemented (`LatticeLog.Generator`) |
| F2 | Free-text and exception API with a documented cost. | implemented (`LoggerExtensions`) |
| F3 | Immutable context (scopes, operations) and `Activity` capture. | implemented |
| F4 | Local `.llog` persistence with Fast/Balanced/Durable modes and a verifiable ACK. | implemented |
| F5 | Crash recovery without interpreting incomplete bytes; `verify`/`repair`. | implemented |
| F6 | Typed queries with indexes, cursors, EXPLAIN and aggregations. | implemented |
| F7 | Evidence: overview, trace, related, error groups, Context Pack with budget and coverage. | implemented |
| F8 | Lossless export (exchange JSONL) and OTLP/JSON with a loss report; JSONL/text/OTLP import. | implemented |
| F9 | Full CLI and read-only MCP. | implemented (alpha) |
| F10 | `Microsoft.Extensions.Logging` bridge. | implemented (`LatticeLog.Extensions.Logging`) |
| F11 | Server: multi-tenancy, scoped tokens, online admin, replication, web console. | implemented as a single node (P16–P24, alpha) |
| F12 | Cluster, storage tiers. | not started (P19–P20) |

## 2. Durability modes (P00-T07)
Defined normatively in `specs/durability.md`. Summary: `Fast` = accepted in memory; `Balanced` (default) = written to
the OS per batch, periodic fsync (1 s); `Durable` = fsync per batch before acknowledging. Configurable saturation
behaviour: block (with a timeout), drop with a counter, or throw.

## 3. "Lossless" (P00-T06)
- **Structured-exact**: types, values, nulls, attribute order, duplicates, NaN, −0.0, decimal scales and strings with
  unpaired surrogates are rebuilt exactly (property tests and golden files).
- **Raw byte-exact**: text import keeps each line's bytes, the `\r` and a missing final newline.
- Documented exception: keys/names with unpaired surrogates are sanitized to U+FFFD and the record is flagged
  `lossy_import`.

## 4. Configuration (P00-T08)
Proposed precedence: defaults < configuration file < environment variables < explicit code. Remote (server)
configuration will **not** be able to change durability or redaction silently: every change is recorded. Today only
code configuration exists (`LatticeLoggerOptions`, `StoreWriterOptions`), with validation and actionable messages.

## 5. Platforms (P00-T09)
.NET 10 LTS (Microsoft support until Nov 2028). Target: Windows/Linux/macOS on x64 and ARM64. CI runs on Linux, macOS
and Windows x64; ARM64 is not tested yet. The format is language-independent (a Python reader is included).

## 6. Versions and compatibility (P00-T10)
SemVer per package. `.llog` format: 1.x readers will read every 0.1+ file, checked with frozen golden files; target
compatibility window of N-2 minor versions; deprecations announced one minor version ahead, with a working exporter
(`latticelog export --format llog-jsonl`). See `docs/compatibility.md`.

## 7. Go/no-go criteria (P00-T12)
| decision | go if… | otherwise |
|---|---|---|
| Lattice-S in production | net savings vs plain binary on ≥3 repetitive workloads with no read regression >20 % | simplify the codec |
| Lattice-C | positive net savings on context-rich workloads | use context only as an index |
| Lattice-P | optimistic savings bound ≥5 % over S+deflate on relevant workloads | archive P (do not build) |
| Cluster (P19) | the measured single-node limit is insufficient for real users | stay single-node |
| SaaS (P25) | — | not applicable: the project is free and MIT-licensed, with no SaaS |

## 8. Scope cuts (P00-T15)
Scope rule: prioritize according to §8.6 of the master plan (contract, fidelity, reader, durability and basic security
before scale).
