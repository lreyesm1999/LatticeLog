# MCP evidence optimization V2

Date: 2026-11-24. Runtime: .NET SDK 10.0.404 / runtime 10.0.15, Windows 11. This report records measured results;
it does not claim model-token exactness or causal inference.

## Technical audit

The existing architecture was already well separated and was retained:

- `LatticeLog.Storage` owns the append-only `.llog` format, readers, recovery, footer indexes, block summaries,
  compaction and retention.
- `LatticeLog.Query` parses typed filters, extracts pruning constraints, chooses sequential versus indexed block
  access, evaluates records, paginates with query-bound cursors and exposes execution statistics and a readable plan.
- `LatticeLog.Evidence` groups deterministic fingerprints, resolves stable event references and builds context packs.
- `LatticeLog.Mcp` exposes source-aware MCP contracts and merges per-source evidence.
- `LatticeLog.Sources` discovers and incrementally indexes local sources; the background service can share that work
  across MCP sessions.

All requested tool families already existed: `search_events`, `get_event`, `get_trace`, `get_related_events`,
`get_error_groups`, `get_context_pack`, `get_event_schema`, plus `get_overview`. No duplicate tools or `.llog` format
changes were introduced.

### Findings and disposition

| Finding | Impact | Difficulty / risk | Disposition |
|---|---:|---:|---|
| Context Pack limited only timeline bytes; the envelope, groups and summaries could exceed the requested budget several times | High | Medium | Fixed: measure and trim the complete structured payload |
| Token count was a chars/4 estimate but the contract did not offer a token budget | High | Low | Fixed: `budget_tokens`, explicitly estimated with UTF-8 bytes/4 |
| No progressive detail contract, event/group limits or context continuation | High | Medium | Fixed additively; existing defaults and v1 aliases remain |
| A capped candidate window could make summary distributions look complete | High | Medium | Fixed: exact total/severity aggregate only when the candidate window is capped; other distributions state their scope |
| Operation relations lacked classification, basis and evidence references | High | Low | Fixed for explicit parent-operation edges (`observed`) |
| Multi-source context queries ran serially | Medium | Medium | Fixed: independent per-source reads run in parallel and merge deterministically |
| Session response accounting used UTF-16 character count of the evidence JSON and ignored MCP duplication | Medium | Low | Fixed: exact UTF-8 size of the complete tool result is enforced before return |
| Relation tagging was omitted when multi-source documents were merged | Medium | Low | Fixed |
| No parent span ID exists in the current event model | Medium | High / format compatibility | Not invented; span-parent causality remains unknown |
| No safe result cache key includes authorization identity and active-file state | Medium | High / leakage | Result caching rejected for this increment; schema/metadata caching remains a future measured optimization |
| Query engine already has deterministic plan selection and index statistics | Medium | Low | Reused and surfaced as Context Pack execution evidence; no ML optimizer added |

The codebase-memory graph MCP was not connected in this Codex session. Per `AGENTS.md`, discovery fell back to local
source inspection. The newly installed `codebase-memory` skill does not itself register an MCP server.

## Implementation

Context Pack V2 keeps `format: latticelog.evidence.v1` and adds `context_version: "2"`. New optional MCP arguments are:

- `budget_tokens`, `max_events`, `max_groups`;
- `detail_level=summary|investigation|evidence`;
- `include_evidence` and `continuation_token`.

Selection stays deterministic: error fingerprint coverage, scope bounds, warnings, explicit neighbours and temporal
coverage. Lower-priority entries are removed first when the full payload is over budget. Stable `llog://` references
remain resolvable with `get_event`. Relations are labeled `observed` and cite the event refs that contain the explicit
operation-parent identifiers. Candidate-window continuation is opaque and query/parameter-bound.

The implementation intentionally does not infer causes from time order, add an LLM, change `.llog`, cache sensitive
results, or add a heavy tokenizer dependency.

## Benchmark method

`benchmarks/LatticeLog.Benchmarks -- mcp` creates a deterministic 100,000-event store with repetitive errors, one
cross-service trace, one no-trace correlated incident and high-cardinality attributes. It runs 15 repetitions of:

- A: one indexed event;
- B: repetitive errors;
- C: cross-service trace;
- D: incident without Trace ID;
- E: high-cardinality grouping;
- F: eight simultaneous independent investigations (the original baseline file used one call, so F is not compared);
- G: a deliberately incomplete 2 KB pack.

Every scenario checks known evidence. Latency is wall-clock in-process MCP time. Bytes are the MCP tool result after
JSON parsing (content plus structured content); the V2 file also records structured-content bytes separately. Token
figures below are `ceil(transport UTF-8 bytes / 4)`, an estimate rather than a model tokenizer. Raw outputs:
[`mcp-evidence-baseline-v1.json`](mcp-evidence-baseline-v1.json), [`mcp-evidence-v2.json`](mcp-evidence-v2.json),
[`mcp-evidence-v2-small.json`](mcp-evidence-v2-small.json), and
[`mcp-evidence-v2-large.json`](mcp-evidence-v2-large.json).

## Comparative results

| Scenario | Metric | Before | After | Difference |
|---|---|---:|---:|---:|
| A | p95 latency | 12.74 ms | 12.70 ms | −0.3 % |
| B | p95 latency | 83.33 ms | 88.58 ms | +6.3 % |
| B | transport bytes | 56,296 | 34,356 | −39.0 % |
| B | estimated transport tokens | 14,074 | 8,589 | −39.0 % |
| B | allocated bytes | 53,550,917 | 54,130,894 | +1.1 % |
| C | p95 latency | 47.25 ms | 32.92 ms | −30.3 % |
| C | transport bytes | 11,480 | 16,591 | +44.5 % |
| D | p95 latency | 36.65 ms | 36.14 ms | −1.4 % |
| D | transport bytes | 6,937 | 10,436 | +50.4 % |
| G | p95 latency | 43.18 ms | 42.97 ms | −0.5 % |
| G | transport bytes | 10,849 | 5,205 | −52.0 % |
| G | structured bytes | not recorded | 1,924 | within 2,000-byte budget |
| A–G | evidence checks | 7/7 | 7/7 | unchanged |

The V2 raw file records 2,918 estimated structured tokens for B and 481 for G. Those are not directly comparable to
the baseline token column because the first harness version recorded only the combined MCP result.

The main success is bounded evidence size without losing the known incident evidence. The explicit downside is extra
metadata on already-small focused packs (C and D) and a small repetitive-error latency cost (B). These
regressions are kept visible rather than averaged away. With 15 repetitions p95 and p99 select the same sample; larger
runs are needed before treating small latency differences as stable.

### Scale check

| Scenario | 10k p95 / structured bytes | 100k p95 / structured bytes | 1M p95 / structured bytes |
|---|---:|---:|---:|
| B repetitive errors | 16.98 ms / 11,633 | 88.58 ms / 11,672 | 853.26 ms / 11,672 |
| C trace | 6.13 ms / 5,836 | 32.92 ms / 5,856 | 341.20 ms / 5,876 |
| D no Trace ID | 7.08 ms / 3,667 | 36.14 ms / 3,679 | 341.15 ms / 3,690 |
| G 2 KB bound | 61.91 ms / 1,918 | 42.97 ms / 1,924 | 36.41 ms / 1,930 |

Evidence size stays bounded and all checks pass, but focused correlation latency scales poorly when a highly compressed
large segment contains the target. Trace/operation/correlation Bloom keys currently prune whole segments; format 0.3
block summaries cover severity/event/service, not correlation keys. A versioned block-level Bloom or an invalidated
sidecar correlation index is therefore the highest-impact next storage/query experiment. It was not inserted into the
binary format in this increment because compatibility and size cost require their own baseline.

## Verification

- Focused Evidence/MCP suite: 21/21 passed.
- Full solution suite: 240/240 passed in Release after one environmental rerun.
- A prior full-suite attempt hit an `Illegal instruction` CLR crash, consistent with the machine instability already
  documented in `docs/benchmarks.md`; no test assertion failed in that run.
- Baseline before changes: 236/237 passed, with one crash-recovery test failing once and all three isolated variants
  passing immediately afterward.

## Limitations and next investigations

- The benchmark is one Windows machine, one runtime and 15 repetitions; no external model was called.
- Exact model tokens require a consumer-supplied tokenizer. The server reports only a clearly labeled estimate.
- `content` and `structuredContent` are both emitted for compatibility, so transport remains larger than the structured
  payload. A protocol-version/feature-negotiated structured-only response should be evaluated before changing this.
- Service/event distributions come from the candidate window when it is capped; the response labels that boundary.
- Continuation advances to the next candidate window; a context pack is a ranked evidence selection, not exhaustive
  pagination. Use `search_events` when every match is required.
- Parent-span relationships cannot be reconstructed because the event model stores `span_id` but not parent span ID.
- Segment-level correlation Bloom filters do not prevent decoding many blocks inside one large compressed segment;
  the 1M-event scale check measures this directly.
- Result caching needs a key that includes source generation, active/unsealed state, redaction policy and authorization
  identity. It should be added only after a benchmark demonstrates value and isolation tests prove no cross-user leak.
- A deterministic cost model can build on existing `QueryEngine.Explain` and statistics. ML-based planning is not
  justified by the current measurements.
