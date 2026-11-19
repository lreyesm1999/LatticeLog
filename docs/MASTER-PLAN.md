# LatticeLog — Master development plan

> **Living execution document | Version 1.0 | November 18, 2026**  
> **Status at writing time:** planning, no experimental results yet (current status: `ROADMAP.md`)  
> **Scope:** research → SDK library → local engine → AI-friendly queries and readers → distributed server → platform → sustained operation  
> **Initial stack:** .NET 10 LTS / C# for the initial engine and SDK, with the persistence format independent of the language.  
> **The "LatticeLog" name:** provisional at writing time; check legal availability, domains and namesake projects before launch.

> **Note (2026-11-18):** this plan was written before the project became a free, MIT-licensed open-source project.
> The commercial parts (P25 and the SaaS mentions) are kept as originally planned for traceability, but they no longer
> apply: see `docs/product/commercial-decisions.md`.

---

## 0. How to use this document

This file is an **executable, traceable work plan**, not just a conceptual description. It is meant to move forward
phase by phase over the whole product life cycle. Each phase contains goals, dependencies, checkable tasks,
deliverables and an exit gate that prevents moving on while foundations are missing.

**Tracking conventions**:

- `[ ]` pending; `[x]` done and validated with evidence.
- Each task has a stable code (`PXX-TYY`); use it as the issue ID in GitHub/Linear/Jira.
- Do not mark a task complete just because "the code exists": tests, documentation and reproducible evidence are needed.
- When closing a phase, create `docs/decisions/PHASE-XX-REVIEW.md` with links to commits, tests, benchmarks, risks and decisions.
- The proposed quantitative thresholds are **targets/hypotheses**, not achieved results or performance promises.
- Phases labelled **R&D** contain decisions that could turn out negative. A scientifically sound negative result can satisfactorily close a phase and allow continuing with the simpler design.

### Non-negotiable identity and limits

1. **LatticeLog does NOT run AI.** No internal LLM, embeddings, neural classification or dependency on model APIs.
2. **AI-friendly means easy to read for an external AI:** deterministic interfaces, useful context, retrievable evidence, stable references and less redundant information.
3. **Lossless by default:** persisted data must be rebuilt faithfully according to an explicit contract; never collapse or drop records through silent deduplication.
4. **The application's critical path comes first:** capturing an event must add minimal latency, allocations and contention, with explicit durability and overload policies.
5. **No universal superiority:** any "better than X" claim must be bounded to published datasets, configuration, hardware and workloads.
6. **Compatibility and independence:** a versioned binary file, a documented reader, standard exports and the ability to leave LatticeLog without vendor lock-in.
7. **Security from the start:** do not log secrets by default; permissions, isolation and redaction for the tools that read data.
8. **Local first, network optional:** usable with no infrastructure, scaling later to a distributed server and the cloud.

### Definition of the final product

An observability platform **specialized in logs and events** including: multi-language SDKs, a public `.llog` format, local and remote ingestion, structural/contextual/predictive encoders, indexed queries, exact reconstruction, search, trace correlation, CLI, HTTP API, efficient evidence export, a read-only MCP adapter, a multi-user server, a distributed cluster, tiered storage, an admin panel, security, auditing, documentation and permanent maintenance.

**Outside the core**: running AI models, determining root causes by inference, replacing a full metrics/tracing APM, building a general-purpose relational database, guaranteeing zero loss without synchronized persistence, inferring causality from timestamps alone.

---

## 1. Overall map of phases and dependencies

| Stage | Phase | Verifiable result | Key dependency | Milestone |
|---|---|---|---|---|
| Foundations | P00 | Charter, scope and success criteria | — | G0 |
| Foundations | P01 | Semantic model specification and guarantees | P00 | G1 |
| R&D | P02 | Lab, datasets and baselines | P00–P01 | G2 |
| Capture | P03 | .NET SDK and typed event generator | P01 | G3 |
| Persistence | P04 | `.llog` v0.x binary specification | P01–P03 | G4 |
| Persistence | P05 | Queue, writer, WAL, durability policy | P04 | G5 |
| Persistence | P06 | Exact reader, recovery and golden files | P04–P05 | G6 |
| Encoding | P07 | Lattice-S structural codec | P02, P06 | G7 |
| Encoding | P08 | Lattice-C contextual codec | P07 | G8 |
| Encoding | P09 | Lattice-P predictive codec (experimental) | P08 | G9 |
| Encoding | P10 | Adaptive selection and background compaction | P07–P09 | G10 |
| Retrieval | P11 | Indexes and query language | P06, P10 | G11 |
| Reading | P12 | AI-friendly outputs and deterministic Context Packs | P11 | G12 |
| Interoperability | P13 | OpenTelemetry, import and export | P01, P11 | G13 |
| Stable SDK | P14 | Industrial-quality .NET library | P03–P13 | **Local v1.0** |
| Tools | P15 | CLI, embedded reader and multi-platform packaging | P14 | G15 |
| Server | P16 | Remote ingestion and transport protocol | P14–P15 | G16 |
| Server | P17 | Query service, metadata and multi-tenancy | P11–P16 | **Server v1.0** |
| Security | P18 | Privacy, authentication, RBAC, auditability | P16–P17 and earlier controls | G18 |
| Scale | P19 | Sharding, replication, fault tolerance | P17–P18 | G19 |
| Storage | P20 | Hot/warm/cold tiers and object storage | P19 | G20 |
| Ecosystem | P21 | Java, Go, Python and Node.js SDKs | P14–P16 | G21 |
| External integration | P22 | Read-only MCP and tools for IDEs/agents | P12, P18 | G22 |
| User experience | P23 | Operational UI, log explorer and control panel | P17–P18 | G23 |
| Operations | P24 | Observability, SRE, backups and recovery | P19–P23 | G24 |
| Business | P25 | License, plans, usage metering, billing and SaaS | P18–P24 | **Cloud GA** (no longer applies) |
| Final quality | P26 | Certifiable performance, chaos, security and compatibility | All previous | G26 |
| Launch | P27 | Documentation, community, support and distribution | P25–P26 | **Product GA** |
| Continuity | P28 | Later roadmap and a sustainable release process | P27 | Evolution |

> **Dependencies do not imply a monolithic implementation order:** tests, security, documentation and benchmarks are cross-cutting from day one. Do not postpone security until P18 or fault testing until P26.

### Delivery milestones (no promised dates)

- **M0 — Measurable hypothesis:** P00–P02.
- **M1 — Verifiable prototype:** P03–P06.
- **M2 — Encoding research concluded:** P07–P10.
- **M3 — Useful local engine:** P11–P15.
- **M4 — Production-ready server:** P16–P18.
- **M5 — Scalable product:** P19–P24.
- **M6 — Complete platform:** P25–P27.
- **M7 — Ongoing evolution:** P28.

---

## 2. Detailed development phases

### P00 — Vision, scope, requirements and technical governance

**Goal:** settle what will be built, how success will be measured and how hard decisions will be made.  
**Dependencies:** none.  
**Deliverables:** `docs/product/vision.md`, `docs/product/prd.md`, `docs/quality/nonfunctional-requirements.md`, `docs/decisions/ADR-0001.md`.

**Tasks**

- [ ] **P00-T01** Write down the problem, target users (local developer, microservices team, operations, SaaS platform) and concrete scenarios.
- [ ] **P00-T02** Define the three differentiators: efficient capture, specialized storage and better reading for external AI.
- [ ] **P00-T03** Write explicit exclusions: internal AI, silent loss, universal compression, mandatory cloud dependency.
- [ ] **P00-T04** Establish the product surfaces: SDK, `.llog`, CLI, query, API, MCP, server, web console and cloud.
- [ ] **P00-T05** Define initial target SLOs for emission latency, server availability and durability, without faking results.
- [ ] **P00-T06** Pin down what "lossless" means: semantic fidelity for typed events and byte-for-byte fidelity for raw imports in exact-preservation mode.
- [ ] **P00-T07** Define the `Fast`, `Balanced` and `Durable` modes, their ACK semantics, saturation and possible losses.
- [ ] **P00-T08** Design the configuration model and precedence rules (code, config, environment, remote; no secret changes).
- [ ] **P00-T09** Identify Linux/Windows/macOS and x64/ARM64 requirements, compatible frameworks and the support horizon.
- [ ] **P00-T10** Approve version support, deprecation and file compatibility policies.
- [ ] **P00-T11** Build a matrix of technical/scientific/legal risks and assign owners.
- [ ] **P00-T12** Define *go/no-go* decision criteria for prediction, compression, cluster and SaaS.
- [ ] **P00-T13** Choose the initial local architecture on .NET 10 LTS and justify it in an ADR; review compatibility as new versions ship.
- [ ] **P00-T14** Run a preliminary check of the name, domains and dependency licenses before publicizing the brand.
- [ ] **P00-T15** Establish the R&D budget, available hardware, time limits and scope-cut criteria.

**Gate G0:** versioned PRD, use cases, non-goals, metrics and risk matrix reviewed. Nobody should start "optimizing" without an agreed baseline.

### P01 — Semantic event model and stable contracts

**Goal:** design a logical record independent of its binary representation and of the language.  
**Dependencies:** P00.  
**Deliverables:** `specs/event-model.md`, `specs/schema-compat.md`, `specs/context-model.md`, `specs/ordering.md`.

**Tasks**

- [ ] **P01-T01** Define `LogRecord`: `timestamp`, `observed_timestamp`, `severity`, `event_id`, `event_name`, `body`, `attributes`, `resource`, `scope`.
- [ ] **P01-T02** Add optional `trace_id`, `span_id`, `operation_id`, `parent_id` and `correlation_id`, with unambiguous semantics.
- [ ] **P01-T03** Specify base types: signed/unsigned, float, decimal, bool, string, bytes, array, map, null, timestamp, duration, UUID.
- [ ] **P01-T04** Distinguish stable event identity, schema version, record instance and logical operation identity.
- [ ] **P01-T05** Establish naming conventions, reserved fields and limits per field, event and context.
- [ ] **P01-T06** Create a system of event contracts: version, required/optional fields, defaults and compatible/incompatible changes.
- [ ] **P01-T07** Formalize immutable contexts or snapshots/versions to avoid ambiguous inheritance and races.
- [ ] **P01-T08** Define when properties propagate (service, user, deployment) and when they do not (secret data, ephemeral context).
- [ ] **P01-T09** Document partial order: per-producer sequence + local monotonic time + wall clock; never infer global causality from the clock.
- [ ] **P01-T10** Define the explicit relationship between spans, operations and events, even with unsynchronized clocks.
- [ ] **P01-T11** Specify deterministic error fingerprints and their limits, without treating them as equivalent to a root cause.
- [ ] **P01-T12** Design an optional `SourceLocation`: repository, commit, file, function and line, avoiding unnecessary private paths.
- [ ] **P01-T13** Specify how to record exceptions: type, message, frames, original stack and `inner exceptions`.
- [ ] **P01-T14** Include confidentiality metadata, retention categories and a flag for potentially sensitive fields.
- [ ] **P01-T15** Define stable `event_ref` references and their resolution even after compaction and migration between nodes.
- [ ] **P01-T16** Build contract examples in JSON Schema or an equivalent schema, plus evolution cases.
- [ ] **P01-T17** Map explicitly to the OpenTelemetry Logs Data Model and W3C Trace Context.
- [ ] **P01-T18** Publish examples of a correct operation, error, retry, timeout, fan-out and incomplete operation.

**Gate G1:** every property can be serialized/deserialized without ambiguity; the version, identity, order and context rules are documented.

### P02 — Scientific lab, datasets and a fair baseline (R&D)

**Goal:** be able to refute or confirm any improvement with reproducible experiments.  
**Dependencies:** P00–P01.  
**Deliverables:** `research/README.md`, `research/dataset-manifest.yaml`, `benchmarks/baselines/`, `scripts/reproduce.sh`, `reports/baseline-v1.md`.

**Tasks**

- [ ] **P02-T01** Select structured/unstructured public datasets (e.g. Loghub-2.0, with verified licenses and terms).
- [ ] **P02-T02** Prepare datasets of our own from real applications, with anonymization and authorization.
- [ ] **P02-T03** Generate deterministic synthetic datasets with versioned seeds and relationship *ground truth*.
- [ ] **P02-T04** Include HTTP/API, inventory, batch processing, IoT, queues, concurrent operations and rare-failure workloads.
- [ ] **P02-T05** Include anti-cases: random strings, high-cardinality fields, truncated traces, missing context, out-of-order messages.
- [ ] **P02-T06** Version the corpus with hash, size, origin, license and cleaning rules; never upload sensitive data.
- [ ] **P02-T07** Implement a generator of 100k/1M/10M/100M events with configurable cardinality and repetition.
- [ ] **P02-T08** Implement a replayer at variable rates and increasing concurrency, with measurable producer latencies.
- [ ] **P02-T09** Prepare `raw text`, `JSONL`, `JSONL+Zstd`, `JSONL+LZ4` and simple binary format baselines.
- [ ] **P02-T10** Evaluate CLP and research alternatives if reproducible implementations with compatible licenses exist.
- [ ] **P02-T11** Measure total bytes including metadata, indexes, dictionaries, manifests and snapshots.
- [ ] **P02-T12** Measure throughput, CPU, memory, GC, p50/p95/p99, ACK latency and selective reads.
- [ ] **P02-T13** Separate offline compression from live ingestion and isolate the cost of compaction.
- [ ] **P02-T14** Create a fixed set of queries: time, service, event, trace, field, text, top errors, correlation.
- [ ] **P02-T15** Automate warm-ups, repetitions, confidence intervals, thermal control, cold/warm cache and recorded hardware.
- [ ] **P02-T16** Add round-trip validation and canonical hashes of the original data.
- [ ] **P02-T17** Design an external AI-friendly evaluation with incidents of known answer, filtering controls and a token budget.
- [ ] **P02-T18** Publish scripts and results with every configuration needed to replicate them.

**Gate G2:** another machine can run the benchmark and get comparable results; the size and time of the baselines are known.

### P03 — Typed-capture .NET SDK and event compiler

**Goal:** capture structured events with minimal overhead before applying advanced encoding.  
**Dependencies:** P01; use P02 for measurement.  
**Deliverables:** `LatticeLog.Abstractions`, `LatticeLog.Core`, `LatticeLog.Generator`, tests, sample API.

**Tasks**

- [ ] **P03-T01** Design `ILatticeLogger`, `EventDescriptor`, `LoggerOptions`, `EventContext` and the `Flush/Dispose` life cycle.
- [ ] **P03-T02** Create APIs for template, typed, free-text and exception events without hiding their costs.
- [ ] **P03-T03** Implement an incremental Roslyn generator that produces serializers from attributes or explicit definitions.
- [ ] **P03-T04** Validate at compile time duplicate IDs, incompatible types, missing fields, undefined placeholders and schema changes.
- [ ] **P03-T05** Avoid `string.Format`, JSON serialization, reflection and `boxing` on the typed fast path whenever possible.
- [ ] **P03-T06** Instrument allocations with BenchmarkDotNet; define requirements per event class.
- [ ] **P03-T07** Use `Span<T>`, `IBufferWriter<byte>`, pools and reusable buffers where profiling justifies it.
- [ ] **P03-T08** Implement levels, filters and optional sampling with explicit rules, signalling loss by design.
- [ ] **P03-T09** Define scopes and context propagation with `AsyncLocal` where appropriate and without a mandatory dependency.
- [ ] **P03-T10** Design control of PII/secret values and safe defaults for the SDK.
- [ ] **P03-T11** Create a `Microsoft.Extensions.Logging` bridge without imposing LatticeLog on the whole application.
- [ ] **P03-T12** Allow capturing events from `Activity`/trace context and keep the metadata.
- [ ] **P03-T13** Create sample ASP.NET Core, Worker Service and Console integrations.
- [ ] **P03-T14** Validate behaviour in an application with an unhandled exception, cancellation and abrupt shutdown.
- [ ] **P03-T15** Measure the cost with logging off, buffered, flushed and under saturation.
- [ ] **P03-T16** Document contracts and usage warnings in the `README` and XML docs.

**Gate G3:** the SDK consumes and produces correct events, with no GC surprises and clear semantics for filtering and persistence state.

### P04 — Open `.llog` binary specification and versioning

**Goal:** establish a verifiable, self-contained and evolvable storage contract.  
**Dependencies:** P01, P03.  
**Deliverables:** `specs/llog-format-v0.md`, golden binary cases and a minimal reference parser.

**Tasks**

- [ ] **P04-T01** Choose byte order, magic bytes, `major/minor`, features and block size limits.
- [ ] **P04-T02** Define the file header, segment header, schema catalog, context table, streams, indexes, checkpoints and footer.
- [ ] **P04-T03** Formalize base encodings: varint, ZigZag, fixed width, bit packing, bytes and UTF-8 strings.
- [ ] **P04-T04** Establish record framing and protection against malformed input/overflow.
- [ ] **P04-T05** Include codec versioning and the descriptors needed to decode without the original SDK.
- [ ] **P04-T06** Define `record_id`, `segment_id`, a per-writer sequence and a stable `logical_ref`.
- [ ] **P04-T07** Define per-block and manifest checksums; a CRC for accidental integrity does not replace cryptographic authentication.
- [ ] **P04-T08** Specify atomic segment commit, manifests, journal and the recovery procedure.
- [ ] **P04-T09** Define checkpoints and bounded state for random queries.
- [ ] **P04-T10** Distinguish the hot capture format from the consolidated cold format; define an exact conversion path.
- [ ] **P04-T11** Define optional extensions and the behaviour with unknown versions/fields.
- [ ] **P04-T12** Document decompression/expansion limits and prevention of home-grown zip bombs.
- [ ] **P04-T13** Design a specification independent of .NET, with no CLR object serialization.
- [ ] **P04-T14** Build a corpus of tiny, huge, empty, truncated and corrupt files.
- [ ] **P04-T15** Design a safe procedure for format migration and version rollback.
- [ ] **P04-T16** Publish hexadecimal examples and public test vectors.

**Gate G4:** at least one second, independent parser can interpret the golden files using only the specification.

### P05 — Ingestion pipeline, WAL and durable writes

**Goal:** a producer→buffer→disk channel with measurable durability and controlled overhead.  
**Dependencies:** P04.  
**Deliverables:** writer, WAL, backpressure policies, stress tests and fault injection.

**Tasks**

- [ ] **P05-T01** Compare per-core SPSC/MPSC, a blocking queue and a bounded ring buffer with real workloads.
- [ ] **P05-T02** Select the implementation after benchmarking p99, contention and memory use.
- [ ] **P05-T03** Implement hot append-only framing and record batching.
- [ ] **P05-T04** Design memory limits, batch size, and flushing by volume/time, not just fixed thresholds.
- [ ] **P05-T05** Define durability modes: accepted-in-memory, written-to-OS, fsync-confirmed; name the ACK in a verifiable way.
- [ ] **P05-T06** Implement a configurable saturation policy: block, fail explicitly, or drop with a counter and an alert.
- [ ] **P05-T07** Add clean shutdown, `FlushAsync`, cancellation and configurable maximum times.
- [ ] **P05-T08** Design a WAL or transactional journal for metadata and segment transitions.
- [ ] **P05-T09** Implement rotation by size/time, a disk quota and protection policies against a full disk.
- [ ] **P05-T10** Test forced interruption during write, fsync, rotation, commit and index update.
- [ ] **P05-T11** Test real file errors: permissions, full disk, read-only, invalid path, locks and extreme latency.
- [ ] **P05-T12** Define behaviour with several processes on the same directory: explicit prohibition or proven coordination.
- [ ] **P05-T13** Measure write amplification and durability on SSD/HDD/NVMe under different policies.
- [ ] **P05-T14** Instrument metrics for the queue, dropped events, pending bytes and confirmation time.
- [ ] **P05-T15** Implement recovery that never interprets incomplete bytes as valid events.

**Gate G5:** recovery demonstrated against repeated crashes and guarantees documented for each mode, without claiming zero loss when there is only a RAM buffer.

### P06 — Reference reader, exact reconstruction and compatibility

**Goal:** show that every accepted file can be read safely and rebuilt without loss.  
**Dependencies:** P04–P05.  
**Deliverables:** reader library, CLI `verify`, test vectors and round-trip validation.

**Tasks**

- [ ] **P06-T01** Implement a streaming parser that does not load the whole file.
- [ ] **P06-T02** Implement a decoder for records, schemas, scopes, contexts and versions.
- [ ] **P06-T03** Ensure exact reconstruction of types, values, nulls, defined order and exceptions.
- [ ] **P06-T04** For raw import, keep bytes, encoding, delimiters and order when byte-exact mode is requested.
- [ ] **P06-T05** Implement `verify` for checksums, manifests, pointers and references.
- [ ] **P06-T06** Allow partial reads of healthy segments when others are corrupt, reporting the gaps.
- [ ] **P06-T07** Handle unsupported formats with clear errors, without dangerous reinterpretation.
- [ ] **P06-T08** Fuzz the parser with malformed files and intentional field corruption.
- [ ] **P06-T09** Create `decode(encode(x)) = x` property tests for every type.
- [ ] **P06-T10** Generate frozen golden files for compatibility tests in future releases.
- [ ] **P06-T11** Measure the reader's sequential throughput, allocations, CPU and memory.
- [ ] **P06-T12** Implement an evidence manifest that reports skipped files, corrupt segments and gaps.
- [ ] **P06-T13** Test many file sizes, negative timestamps, Unicode, special floats and extreme values.
- [ ] **P06-T14** Produce a simple reference decoder separable from the optimized engine.

**Gate G6:** 100 % of round-trip tests and golden files pass; no silent failures are accepted on damaged input.

### P07 — Lattice-S: structural encoding (R&D)

**Goal:** remove structural redundancy without temporal prediction.  
**Dependencies:** P02, P06.  
**Deliverables:** `Structural` codec, microbenchmarks, a report on savings and costs.

**Tasks**

- [ ] **P07-T01** Separate the event descriptor from dynamic values.
- [ ] **P07-T02** Implement dictionaries of names, types, levels, events and repeated strings.
- [ ] **P07-T03** Evaluate per-block, per-segment, per-source and global dictionaries, including storage and rebuild cost.
- [ ] **P07-T04** Implement delta/varint for timestamps and suitable numeric fields.
- [ ] **P07-T05** Implement bit packing/RLE for small domains and repeated values.
- [ ] **P07-T06** Test row-wise, column-wise and hybrid layouts with several query filters.
- [ ] **P07-T07** Design a low-cost raw fallback for high-entropy fields.
- [ ] **P07-T08** Measure the impact of cardinality and block size.
- [ ] **P07-T09** Establish a dictionary growth limit and a reset policy.
- [ ] **P07-T10** Verify that the original order and metadata are kept according to the contract.
- [ ] **P07-T11** Measure CPU and throughput overhead as well as size ratio.
- [ ] **P07-T12** Compare against JSON+Zstd, plain binary and applicable baselines.
- [ ] **P07-T13** Write the format decision with positive and negative results.

**Gate G7:** a reversible codec, reproducible results, and clarity about when it beats plain binary and when it does not.

### P08 — Lattice-C: context inheritance (R&D)

**Goal:** encode the common properties of an execution without repeating them and without inferring relationships that do not exist.  
**Dependencies:** P07.  
**Deliverables:** context model, shared references, differential benchmark.

**Tasks**

- [ ] **P08-T01** Implement immutable context snapshots and versioned references.
- [ ] **P08-T02** Decide the context level (service, process, request, span, operation) and scoping rules.
- [ ] **P08-T03** Implement explicit inheritance and a list of overrides, removals and local values.
- [ ] **P08-T04** Prevent sensitive contexts from propagating implicitly.
- [ ] **P08-T05** Measure the context reuse rate in real applications.
- [ ] **P08-T06** Implement context-graph compaction, controlling cycles and orphaned references.
- [ ] **P08-T07** Guarantee events are recoverable even when their context lives in another block.
- [ ] **P08-T08** Design checkpoints for random access without walking the whole operation.
- [ ] **P08-T09** Model concurrency, span fan-out, retries and partially instrumented paths.
- [ ] **P08-T10** Design a direct fallback when the context's cost exceeds its savings.
- [ ] **P08-T11** Compare Lattice-C vs Lattice-S with data that does and does not have explicit context.
- [ ] **P08-T12** Publish a report of net bytes, lookups, RAM and search times.

**Gate G8:** measurable net savings on context-rich workloads with no prohibitive read degradation; the reader never assumes unprovable contexts.

### P09 — Lattice-P: encoding transitions and residuals (R&D)

**Goal:** check whether temporal patterns and explicit dependencies allow more efficient encoding than Lattice-C.  
**Dependencies:** P08.  
**Deliverables:** `Predictive` codec, ablation benchmarks, formal documentation of the predictor state.

**Tasks**

- [ ] **P09-T01** Define a deterministic, versioned predictor contract with `predict(state, event_schema)` and `update(state, actual)`.
- [ ] **P09-T02** Formalize event sequences without inventing missing records and without reordering events from independent producers.
- [ ] **P09-T03** Investigate encoding with dictionaries of frequent sequences within a context.
- [ ] **P09-T04** Implement a frequent-transition detector with bounded memory and CPU.
- [ ] **P09-T05** Implement `PatternID + deviations` for exact sequences and explicit deviations.
- [ ] **P09-T06** Implement reversible prediction of timestamps/counters/numbers and store exact residuals.
- [ ] **P09-T07** Consider XOR for floats and binary data when it beats arithmetic differences.
- [ ] **P09-T08** Handle schema changes, session breaks, null fields, missing events and incomplete operations.
- [ ] **P09-T09** Version the states and snapshots needed for a future reader to reproduce predictions.
- [ ] **P09-T10** Add state checkpoints so that a read does not depend on hours of history.
- [ ] **P09-T11** Measure residual entropy and the total size of the pattern dictionary.
- [ ] **P09-T12** Implement a direct fallback algorithm when there is no amortized gain.
- [ ] **P09-T13** Run an ablation analysis: S, C, C+transitions, C+residuals, full P.
- [ ] **P09-T14** Test adversarial datasets with rare sequences and random data.
- [ ] **P09-T15** Measure the impact on p99 and the compaction cost under real load.
- [ ] **P09-T16** Document whether prediction is kept, limited to certain patterns or dropped.

**Gate G9:** a net improvement over Lattice-C on relevant workloads, including models, dictionaries, checkpoints, CPU and queries. If it fails, keep Lattice-C as the main engine and archive Lattice-P as an experiment; do not force it into production.

### P10 — Adaptive planner, compaction, cost policies

**Goal:** choose per segment/field the codec that adds the most value without affecting capture.  
**Dependencies:** P07–P09.  
**Deliverables:** `CodecPlanner`, `Compactor`, multivariate benchmark, rollback mechanisms.

**Tasks**

- [ ] **P10-T01** Define the codec interface (`estimate`, `encode`, `decode`, `index_capabilities`, `metadata_cost`).
- [ ] **P10-T02** Implement statistical sampling of a segment to estimate cardinality and repetition.
- [ ] **P10-T03** Design the cost function: bytes + write/compaction CPU + read latency + index cost.
- [ ] **P10-T04** Implement `Fast`, `Balanced` and `Compact` policies, with configurable weights and safe defaults.
- [ ] **P10-T05** Allow comparing candidate alternatives without destabilizing the hot ingestion path.
- [ ] **P10-T06** Select a codec per column/field when it pays off; document the format's limits.
- [ ] **P10-T07** Add a raw fallback and an optional general-purpose codec (Zstd/LZ4) for blocks that cannot be structured.
- [ ] **P10-T08** Implement asynchronous compaction of hot segments into warm/cold segments.
- [ ] **P10-T09** Create an atomic conversion: write the new segment, validate, activate the manifest and only then retire the original.
- [ ] **P10-T10** Design compaction idempotency and retries after a crash.
- [ ] **P10-T11** Keep logical references stable through a location resolution/mapping table.
- [ ] **P10-T12** Limit the compactor's CPU, IO, RAM and bandwidth so it does not compete with the application.
- [ ] **P10-T13** Expose metrics for ratio, compaction debt, write amplification and search cost.
- [ ] **P10-T14** Implement reorganization/reindexing without losing audit metadata.
- [ ] **P10-T15** Measure the effective corpus size after compaction and temporary duplicated storage.
- [ ] **P10-T16** Publish a decision per workload: which codecs to use and under which thresholds.

**Gate G10:** reproducible and recoverable files, resumable compaction and a verifiable advantage per policy; `no-compaction` is supported for simple operation.

### P11 — Indexes, queries and selective retrieval

**Goal:** make it possible to find useful events without walking, decoding or exporting the whole history.  
**Dependencies:** P06, P10.  
**Deliverables:** `LatticeLog.Query`, query language v1, pagination and explain APIs.

**Tasks**

- [ ] **P11-T01** Inventory concrete queries and establish a functional coverage matrix.
- [ ] **P11-T02** Design a safe query AST and syntax, with typed filters and no arbitrary code execution.
- [ ] **P11-T03** Implement filters for time, service, EventID/EventName, severity and `trace_id`.
- [ ] **P11-T04** Add predicates over typed fields (`=`, `!=`, ranges, exists, contains, begins-with depending on the type).
- [ ] **P11-T05** Design per-segment indexes: min/max, event presence, counts and probabilistic filters.
- [ ] **P11-T06** Implement locators by trace/operation/event reference.
- [ ] **P11-T07** Investigate Bloom/Xor filters and measure size/false positives.
- [ ] **P11-T08** Build selective indexes for high-value fields, avoiding indexing everything.
- [ ] **P11-T09** Implement text search in a later phase or as an adapter, with clear guarantees.
- [ ] **P11-T10** Implement a query planner that uses indexes before decoding streams.
- [ ] **P11-T11** Implement partial reads of columns/fields where the format allows.
- [ ] **P11-T12** Create stable pagination by cursor and snapshot version; avoid duplicates/skips caused by compaction.
- [ ] **P11-T13** Add configurable limits on time, memory, results and response bytes.
- [ ] **P11-T14** Support cancelling queries and isolating them from the writer.
- [ ] **P11-T15** Add aggregations: counts by event/severity, time series, top fingerprints.
- [ ] **P11-T16** Add `EXPLAIN` to show the segments queried, indexes used and estimated cost.
- [ ] **P11-T17** Document consistency semantics: snapshot, near-real-time, eventual, and incomplete data.
- [ ] **P11-T18** Create correctness tests against a full-scan oracle; verify identical results.
- [ ] **P11-T19** Compare the latency of 10–20 pattern queries and resource usage with baselines.
- [ ] **P11-T20** Design a stable API for local queries and a future remote server.

**Gate G11:** correctness against a full scan, queries without global decoding, robust pagination and an explanation of gaps/limitations.

### P12 — AI-friendly reading without AI: Context Packs and evidence

**Goal:** any external tool —a person, a script or an AI— gets a more useful and cheaper context than a log dump.  
**Dependencies:** P11.  
**Deliverables:** `specs/evidence-protocol.md`, compact JSONL/JSON, CLI context, a quality evaluator.

**Principle:** **LatticeLog only structures, filters, groups and retrieves; it never interprets with models or automatically asserts a root cause.**

**Tasks**

- [ ] **P12-T01** Define a canonical public JSON output for events, without exposing the binary directly.
- [ ] **P12-T02** Specify a stable `event_ref` and an endpoint/command to retrieve the original event.
- [ ] **P12-T03** Implement an `overview` per window: services, events, errors, counts and ranges.
- [ ] **P12-T04** Implement `get_trace`: an ordered timeline, explicit relationships and relative timestamps.
- [ ] **P12-T05** Implement `get_related_events` using trace/span/operation and declared links; distinguish a relationship from a temporal coincidence.
- [ ] **P12-T06** Implement deterministic grouping by configurable fingerprints, returning counts and referenced samples.
- [ ] **P12-T07** Design Context Pack v1 per incident/trace/time range, with selection by an explicit relevance heuristic.
- [ ] **P12-T08** Apply a budget in bytes/characters and optionally estimated tokens; never guarantee exact tokens across models.
- [ ] **P12-T09** Preserve coverage: totals, amount returned, excluded segments, truncation, sampling, filters and gaps.
- [ ] **P12-T10** Ensure every numeric summary comes from computed data, not invented text.
- [ ] **P12-T11** Add `overview`, `trace` and `evidence` levels of detail and progressive access.
- [ ] **P12-T12** Optionally add `repo/commit/function` code context, only when it is recorded and authorized.
- [ ] **P12-T13** Expose the `schema`, event descriptions, types and a readable semantic dictionary.
- [ ] **P12-T14** Support `json`, `jsonl`, `markdown` and `ndjson` formats, with field normalization.
- [ ] **P12-T15** Apply secret redaction and authorization to every output.
- [ ] **P12-T16** Escape and label untrusted text coming from logs so that clients do not interpret it as instructions.
- [ ] **P12-T17** Design an external benchmark: same incident/model/questions; compare raw text, standard JSON and a Context Pack.
- [ ] **P12-T18** Measure tokens, relevant evidence retrieved, false diagnoses, number of queries and total latency.
- [ ] **P12-T19** Create an "incident ground truth" dataset with known causal errors and rare events.
- [ ] **P12-T20** Version the `EvidenceProtocol` independently of the physical `.llog`.

**Gate G12:** deterministic, verifiable outputs with no silent omissions; show real value for external readers without integrating any LLM.

### P13 — Interoperability: OpenTelemetry, traces and existing formats

**Goal:** prevent the product from only working when the whole company switches SDKs.  
**Dependencies:** P01, P11, P12.  
**Deliverables:** OTel/ILogger bridges, importers, exporters, semantic round-trip tests.

**Tasks**

- [ ] **P13-T01** Define a bidirectional mapping between LatticeLog and the OpenTelemetry Logs Data Model.
- [ ] **P13-T02** Map `Resource`, `InstrumentationScope`, `Severity`, `Attributes`, `TraceId`, `SpanId`, timestamps and EventName.
- [ ] **P13-T03** Implement JSONL and OTLP exporters in a compatible protocol version.
- [ ] **P13-T04** Implement an optional OpenTelemetry/OTLP log receiver, with limits and validation.
- [ ] **P13-T05** Implement a structured JSONL importer and raw preservation when requested.
- [ ] **P13-T06** Implement an optional free-text parser and report parsing precision/ambiguity.
- [ ] **P13-T07** Keep `TraceContext` and baggage only according to explicit policies; never copy secrets.
- [ ] **P13-T08** Implement ILogger/Serilog/NLog bridges as adapters, verifying versions.
- [ ] **P13-T09** Define the unavoidable loss of expressiveness between models and how to report it (never hide it).
- [ ] **P13-T10** Design a complete export that allows leaving the `.llog` format.
- [ ] **P13-T11** Evaluate integration with the OpenTelemetry Collector, Kubernetes log files and containers.
- [ ] **P13-T12** Test timestamps, severity levels and attribute structures from each source.
- [ ] **P13-T13** Publish a gradual migration cookbook from existing logs.
- [ ] **P13-T14** Validate that the OTel integration does not impose a second costly pipeline on sensitive applications.

**Gate G13:** import/export with documented fidelity, without forcing the adoption of a new telemetry stack.

### P14 — Production-ready .NET SDK (Local v1.0)

**Goal:** turn the prototype into a safe library for real applications.  
**Dependencies:** P03–P13.  
**Deliverables:** NuGet packages, compatibility, a stable API, an upgrade guide, a local release candidate.

**Tasks**

- [ ] **P14-T01** Freeze minimal public contracts and the error design.
- [ ] **P14-T02** Ensure `Dispose/DisposeAsync` semantics, flush order, shutdown timeout and crash handling.
- [ ] **P14-T03** Review allocations and p99 in synchronous/asynchronous logging under several workloads.
- [ ] **P14-T04** Verify thread safety and the absence of deadlocks under saturation, recursion and logging during failures.
- [ ] **P14-T05** Implement configuration validation with actionable error messages.
- [ ] **P14-T06** Optimize cold start, baseline memory and resource use when there are no events.
- [ ] **P14-T07** Add minimal internal diagnostics without recursive logging loops.
- [ ] **P14-T08** Review trimming, Native AOT and single-file deployment compatibility.
- [ ] **P14-T09** Test Linux, Windows and macOS; separate official support per architecture.
- [ ] **P14-T10** Set up signed packages where it makes sense, symbols, SourceLink and an SBOM.
- [ ] **P14-T11** Freeze golden files and N-1/N-2 compatibility tests.
- [ ] **P14-T12** Run an internal audit of secrets, paths, permissions and limit evasion.
- [ ] **P14-T13** Test sample applications with real logs and perf profiles.
- [ ] **P14-T14** Publish `getting-started`, breaking changes and troubleshooting.
- [ ] **P14-T15** Release an RC; handle critical issues and reproduce public benchmarks.
- [ ] **P14-T16** Release local v1.0 only if the durability policy is proven.

**Gate Local v1.0:** a stable API, compatible reader and writer, recoverable failures, usable documentation and declared SLOs with measured results.

### P15 — CLI, developer tools and local distribution

**Goal:** make LatticeLog useful outside the process and from development environments.  
**Dependencies:** P14.  
**Deliverables:** a CLI executable and documented commands, distribution packages, examples.

**Tasks**

- [ ] **P15-T01** Create the `init`, `tail`, `inspect`, `query`, `trace`, `context`, `verify`, `repair`, `compact`, `stats` and `export` commands.
- [ ] **P15-T02** Design human-readable output by default and a stable `--format json/jsonl` for scripts.
- [ ] **P15-T03** Establish predictable exit codes and errors with a machine-readable structure.
- [ ] **P15-T04** Create pagination/cursors for large outputs; limit accidental exports of gigabytes.
- [ ] **P15-T05** Implement `tail` over growing segments with partial-read semantics.
- [ ] **P15-T06** Add `--budget-bytes` and redaction for `context`.
- [ ] **P15-T07** Implement maintenance commands that work without a server.
- [ ] **P15-T08** Distribute self-contained or Native AOT binaries where feasible.
- [ ] **P15-T09** Publish installers/packages per operating system; sign and verify integrity.
- [ ] **P15-T10** Create shell completion, help and working examples.
- [ ] **P15-T11** Test Windows/Linux/macOS installers and their permissions in CI.
- [ ] **P15-T12** Test automation in pipelines and the isolation of command output logs.
- [ ] **P15-T13** Document migration, export and the data recovery procedure.

**Gate G15:** any developer can create, query, verify and export logs with the CLI without programming or installing a server.

### P16 — Remote ingest protocol and collector

**Goal:** receive events from many applications without dropping local storage as an option.  
**Dependencies:** P14–P15.  
**Deliverables:** collector service, client-server protocol, retry/ACK policy, network tests.

**Tasks**

- [ ] **P16-T01** Define the initial transport (e.g. gRPC/HTTP2 or HTTP batches) and justify its SDK cost.
- [ ] **P16-T02** Design a sending envelope with `producer_id`, sequence, schema, batch ID and checksum.
- [ ] **P16-T03** Define `at-least-once` or equivalent delivery semantics; do not promise exactly-once without a complete protocol.
- [ ] **P16-T04** Implement idempotency and deduplication by batch/event sequence, keeping distinct events.
- [ ] **P16-T05** Configure the ACK according to the server's local persistence and replica state when applicable.
- [ ] **P16-T06** Implement retries with backoff/jitter and limits.
- [ ] **P16-T07** Implement a bounded local spool for connectivity failures.
- [ ] **P16-T08** Define what happens when the spool is full and how it is reported to the producer.
- [ ] **P16-T09** Support negotiation of schema/codec versions and fallbacks.
- [ ] **P16-T10** Implement TLS, client authentication and credential rotation from the start.
- [ ] **P16-T11** Apply per-client quotas, message size and network backpressure.
- [ ] **P16-T12** Design protection against huge events and decompression bombs.
- [ ] **P16-T13** Create network tests: packet loss, timeouts, outages, duplicates, slow networks and reordering.
- [ ] **P16-T14** Measure end-to-end throughput, spool memory and ACK latency.
- [ ] **P16-T15** Document the limits of the guarantees when an application crashes before persisting or transmitting.

**Gate G16:** a producer can disconnect, retry and recover without silent duplicates or false confirmations.

### P17 — Query server, catalog and multi-tenancy (Server v1.0)

**Goal:** enable centralized storage and reading with roles and namespaces.  
**Dependencies:** P11, P16.  
**Deliverables:** `latticelog-server`, metadata catalog, HTTP/gRPC read API, Docker deployment.

**Tasks**

- [ ] **P17-T01** Design `tenant`, `project`, `environment`, `service`, `source` and `namespace`.
- [ ] **P17-T02** Separate the metadata/control plane from heavy data, with transactional persistence.
- [ ] **P17-T03** Implement a catalog of segments, manifest, schemas, contexts, indexes and compaction state.
- [ ] **P17-T04** Create endpoints for ingestion, search, get event, get trace, aggregations, export and context pack.
- [ ] **P17-T05** Implement minimal authentication/authorization from the server's first beta.
- [ ] **P17-T06** Enforce per-user/tenant quotas for queries, bytes, concurrency and ingestion.
- [ ] **P17-T07** Implement signed cursors, or cursors protected against tampering, with expiry.
- [ ] **P17-T08** Separate slow queries from ingestion, with independent pools and limits.
- [ ] **P17-T09** Define query snapshots and consistency after compaction.
- [ ] **P17-T10** Allow reading recent data and compacted segments with a single planner.
- [ ] **P17-T11** Document the semantics of arrival timestamps vs event time.
- [ ] **P17-T12** Implement administrative tasks for retention, compaction and reindexing.
- [ ] **P17-T13** Test metadata migrations and rollback where feasible.
- [ ] **P17-T14** Build a minimal Docker image, configuration through env/secrets and health endpoints.
- [ ] **P17-T15** Write a simple single-machine deployment guide, with no mandatory K8s.
- [ ] **P17-T16** Run load tests with many producers and concurrent readers.

**Gate Server v1.0:** multiple sources ingest and query safely, with recoverable persistence and without requiring a cluster.

### P18 — Security, privacy, governance and auditability

**Goal:** protect information that may include secrets, PII and sensitive evidence.  
**Dependencies:** apply the minimums from P01, harden P16–P17.  
**Deliverables:** threat model, RBAC policies, security test suite, review report.

**Tasks**

- [ ] **P18-T01** Threat-model the SDK, binary, parser, collector, query, MCP, UI, server and cloud.
- [ ] **P18-T02** Design data classification and capture policies (`never-log`, `redact`, `hash`, `allow-list`).
- [ ] **P18-T03** Implement producer-side redaction where feasible, before secrets are persisted.
- [ ] **P18-T04** Deploy TLS for every remote connection and encryption at rest depending on the deployment mode.
- [ ] **P18-T05** Manage keys through a KMS/secret store when one exists; never include secrets in published configuration files.
- [ ] **P18-T06** Define RBAC/ABAC per tenant, project, service, environment and field where applicable.
- [ ] **P18-T07** Establish specific read-only access for external tools and time-limited tokens.
- [ ] **P18-T08** Create an immutable or protected audit log of access, exports and administrative operations.
- [ ] **P18-T09** Mitigate costly queries, filter abuse, path traversal, parser attacks and resource exhaustion.
- [ ] **P18-T10** Treat log text as untrusted content in HTML, Markdown and MCP tools.
- [ ] **P18-T11** Test tenant isolation with automated negative authorization suites.
- [ ] **P18-T12** Implement deletion by retention and privacy requests, with explicit rules for backups and replication.
- [ ] **P18-T13** Design legal hold and evidence controls when needed.
- [ ] **P18-T14** Set up SAST, dependency scanning, secret scanning, an SBOM and vulnerability reporting.
- [ ] **P18-T15** Design incident response: rotation, revocation, disclosure, CVEs, patches.
- [ ] **P18-T16** Get an independent review/pentest before handling third-party data.
- [ ] **P18-T17** Review applicable laws and obligations per jurisdiction with specialized advice, without promising automatic certification.
- [ ] **P18-T18** Verify that Context Packs do not drop sensitive-data labels or reveal partially authorized fields.

**Gate G18:** successful multi-tenant negative tests, encryption/credentials correctly implemented and a vulnerability/privacy policy in place.

### P19 — Horizontal scalability, partitioning and replication

**Goal:** support growth without depending on a single node and keep the guarantees during network failures.  
**Dependencies:** P17–P18.  
**Deliverables:** minimum viable cluster, rebalancing, replication, chaos tests.

**Tasks**

- [ ] **P19-T01** Measure single-node server limits before justifying distribution.
- [ ] **P19-T02** Define shard units (tenant/project/source/time bucket) and their compatibility with queries.
- [ ] **P19-T03** Design an ingestion router and query fan-out with concurrency limits.
- [ ] **P19-T04** Choose a control-plane/metadata store for members, assignments and manifests.
- [ ] **P19-T05** Specify a leader/epoch/fencing mechanism to prevent concurrent valid writers after a partition.
- [ ] **P19-T06** Define the consistency model for indexes, metadata and data (quorum or leader append, depending on the design).
- [ ] **P19-T07** Implement segment replication and acknowledge only according to the configured policy.
- [ ] **P19-T08** Design duplicate detection by batch/sequence and resumption after failover.
- [ ] **P19-T09** Create snapshots and rebuild indexes/catalog from segments.
- [ ] **P19-T10** Implement shard movement, backfill and throttling.
- [ ] **P19-T11** Prevent compaction from breaking logical references during migration between nodes.
- [ ] **P19-T12** Apply quotas and resource isolation on shared nodes.
- [ ] **P19-T13** Implement partial queries with `partial_results` and clearly indicated missing shards.
- [ ] **P19-T14** Test network partitions, leader crashes, full disks, split-brain and replica lag.
- [ ] **P19-T15** Benchmark scaling across 1, 3, 5 and N nodes and quantify the cost per TB/event.
- [ ] **P19-T16** Document recommended topologies, limits and a recovery runbook.

**Gate G19:** no node failure leads to false confirmations or silent corruption; horizontal scaling measured and consistency limits documented.

### P20 — Tiered storage, object storage and retention

**Goal:** reduce the cost of history without losing selective search and governance.  
**Dependencies:** P19 (a single-node variant can be prototyped earlier).  
**Deliverables:** hot/warm/cold life cycle, storage adapters and a retention engine.

**Tasks**

- [ ] **P20-T01** Define `hot` (writing), `warm` (indexed/compacted) and `cold` (cheap objects) segments.
- [ ] **P20-T02** Design a life cycle by age, access, size and tenant policy.
- [ ] **P20-T03** Implement object storage (S3-compatible first) and checksum validation.
- [ ] **P20-T04** Design the manifest and atomic commit with eventually consistent object storage and caches.
- [ ] **P20-T05** Implement selective rehydration for historical queries.
- [ ] **P20-T06** Keep a compact index of cold objects so they are not downloaded whole.
- [ ] **P20-T07** Design an LRU/ARC cache or an alternative based on metrics and per-tenant limits.
- [ ] **P20-T08** Implement data expiry and deletion with verification of shared references.
- [ ] **P20-T09** Add retention rules per project, category and severity, with explicit legal exceptions.
- [ ] **P20-T10** Design snapshot/backup maintenance and consistency with object storage.
- [ ] **P20-T11** Test connection loss, object corruption, orphaned objects and manifest recovery.
- [ ] **P20-T12** Measure cold-query p95 latency, storage cost and egress cost.
- [ ] **P20-T13** Create a cost simulator per month/TB and tiering policies.
- [ ] **P20-T14** Document reversibility: export and recovery without a specific cloud provider.

**Gate G20:** rotation, rehydration, deletion and recovery are tested; references and indexes stay correct across all tiers.

### P21 — Multi-language SDK ecosystem

**Goal:** broaden adoption without duplicating the codec logic in every ecosystem.  
**Dependencies:** P14–P16.  
**Deliverables:** Java, Go, Python and Node.js SDKs with cross-compatibility tests.

**Tasks**

- [ ] **P21-T01** Define the SDK architecture: simple transport clients; a native `.llog` writer only where justified.
- [ ] **P21-T02** Create a protocol specification and language-independent contract tests.
- [ ] **P21-T03** Publish a library of test vectors for types, values, exceptions and context.
- [ ] **P21-T04** Implement a Java SDK (optional SLF4J/Log4j2 bridge, context propagation, batching).
- [ ] **P21-T05** Measure Java runtime costs and ensure correct behaviour with virtual threads where applicable.
- [ ] **P21-T06** Implement a Go SDK with typed fields, batching and a blocking policy.
- [ ] **P21-T07** Evaluate a `log/slog` bridge, without imposing our own interface on every application.
- [ ] **P21-T08** Implement a Python SDK compatible with `logging`/structlog, with controlled background shipping.
- [ ] **P21-T09** Design multiprocessing and shutdown semantics for Python.
- [ ] **P21-T10** Implement a Node.js/TypeScript SDK with a worker transport where it helps.
- [ ] **P21-T11** Offer a Pino bridge in Node.js and documentation of event-loop behaviour.
- [ ] **P21-T12** Keep the same schema, severity and trace context model across all SDKs.
- [ ] **P21-T13** Add TLS certificates, authentication and retries with consistent semantics.
- [ ] **P21-T14** Publish official packages on NuGet, Maven Central, PyPI, npm and Go modules.
- [ ] **P21-T15** Test cross-compatibility of N-1/N-2 protocol versions.
- [ ] **P21-T16** Create examples of interconnected microservices in different languages.
- [ ] **P21-T17** Measure each SDK's overhead against the ecosystem's base logging; do not extrapolate .NET benchmarks.
- [ ] **P21-T18** Create a maintenance policy per version/runtime for each SDK.

**Gate G21:** the SDKs pass contract tests and the loss, retry, authentication and export semantics; reading the format stays independent of the producer.

### P22 — MCP adapter and tools for external agents (without running AI)

**Goal:** make it easy for any IDE/agent/external AI to query evidence with few calls and without excessive privileges.  
**Dependencies:** P12, P18.  
**Deliverables:** read-only `latticelog-mcp`, tool contracts, conformance and security tests.

**Tasks**

- [ ] **P22-T01** Identify the MCP standard version current at implementation time and freeze the target version.
- [ ] **P22-T02** Create the `search_events`, `get_event`, `get_trace`, `get_related_events`, `get_error_groups`, `get_context_pack` and `get_event_schema` tools.
- [ ] **P22-T03** Use the same Query Engine services as the CLI/HTTP; avoid duplicated interpretation logic.
- [ ] **P22-T04** Define JSON Schemas for inputs/outputs and stable errors.
- [ ] **P22-T05** Enable a minimal-exposure local stdio transport; remote HTTP only with robust auth.
- [ ] **P22-T06** Implement read-only scopes and mandatory per-tenant/project filters.
- [ ] **P22-T07** Set limits on queries, returned data and the byte budget per session.
- [ ] **P22-T08** Prevent instruction injection: outputs separate trusted metadata from untrusted log text.
- [ ] **P22-T09** Redact or block secrets/PII and apply per-field authorization.
- [ ] **P22-T10** Include provenance, `partial_results`, truncation and a retrievable `event_ref` in every output.
- [ ] **P22-T11** Create connection guides for Cursor, Claude Code, Codex and other compatible clients once support is verified.
- [ ] **P22-T12** Run an MCP conformance suite and limit/abuse tests.
- [ ] **P22-T13** Evaluate quality with an external AI in a separate lab: the same incident and query budget.
- [ ] **P22-T14** Document that MCP is an optional adapter and is not on the capture path.

**Gate G22:** an external AI can query logs with context and evidence safely, but LatticeLog never calls a model by itself.

### P23 — Operations and exploration user interface

**Goal:** provide a human environment to find, query and administer logs without depending on a terminal.  
**Dependencies:** P17–P18.  
**Deliverables:** Web Console, UX design, E2E tests, accessibility.

**Tasks**

- [ ] **P23-T01** Run interviews or tests with development, DevOps and support users.
- [ ] **P23-T02** Define the navigation information architecture: Overview, Search, Traces, Event Schemas, Storage, Sources, Settings.
- [ ] **P23-T03** Design a search view with readable filters, field search, dates and pagination.
- [ ] **P23-T04** Create an event inspector with inherited context, raw fields, schema and a reference link.
- [ ] **P23-T05** Create an operation/trace timeline with explicit relationships and gap markers.
- [ ] **P23-T06** Add a view of fingerprints and error volume with exact counts or declared approximations.
- [ ] **P23-T07** Show operational costs: hot/warm/cold size, ratios, retention and ingestion.
- [ ] **P23-T08** Add Context Pack export and verifiable links to records.
- [ ] **P23-T09** Design permission, project and API key control with a safe UX.
- [ ] **P23-T10** Implement dark/light themes, a responsive layout and appropriate WCAG accessibility.
- [ ] **P23-T11** Use virtualization and streaming for massive results.
- [ ] **P23-T12** Prevent XSS from log messages and dangerous Markdown/HTML rendering.
- [ ] **P23-T13** Create E2E tests for search, export, auth and tenant isolation.
- [ ] **P23-T14** Measure usability: completed tasks and time to find the evidence of an incident.
- [ ] **P23-T15** Document when the UI shows partial or delayed data.

**Gate G23:** basic user flows work without a terminal, with secure access and clarity about result coverage.

### P24 — Operations, SRE, backup and disaster recovery

**Goal:** operate LatticeLog with visibility, recovery and procedures before accepting production users.  
**Dependencies:** P19–P23.  
**Deliverables:** runbooks, dashboards, alerts, automatic backup/restore and DR tests.

**Tasks**

- [ ] **P24-T01** Define SLIs/SLOs for ingestion, availability, search, replica lag, durability and restore.
- [ ] **P24-T02** Expose the engine's own metrics without creating infinite log loops.
- [ ] **P24-T03** Create dashboards for the queue, ACK failures, compaction, disks, CPU, cache, queries and tenants.
- [ ] **P24-T04** Design alerts with thresholds and actions for saturation, unreplicated data, segment loss and security.
- [ ] **P24-T05** Implement a consistent backup of metadata, manifests and data.
- [ ] **P24-T06** Restore on new infrastructure and verify integrity after recovery.
- [ ] **P24-T07** Define RPO/RTO per deployment, together with the real technical limitations.
- [ ] **P24-T08** Rehearse regional disaster recovery for deployments that need it.
- [ ] **P24-T09** Create runbooks for a downed node, corruption, a full disk, a saturated queue, compromised keys and failed migrations.
- [ ] **P24-T10** Implement canary/rolling deployments and compatibility checks before upgrading.
- [ ] **P24-T11** Test safe rollback across binary, schema and server version changes.
- [ ] **P24-T12** Run drills for availability incidents and loss of connectivity.
- [ ] **P24-T13** Create operational budgets: CPU/hour, cost per TB/month, egress, IO and bandwidth.
- [ ] **P24-T14** Establish on-call, escalation, severities and incident communication.
- [ ] **P24-T15** Prepare documentation for on-premises, Docker and Kubernetes installations.
- [ ] **P24-T16** Establish capacity limits and reproducible sizing guides.

**Gate G24:** restore tested, SLOs observable, runbooks executed and realistic intervention policies.

### P25 — Commercial product, licensing, billing and SaaS (no longer applies)

> **Superseded on 2026-11-18:** LatticeLog is free and MIT-licensed, with no editions, billing or SaaS. The original
> tasks are kept below for traceability only. See `docs/decisions/PHASE-25-REVIEW.md`.

**Goal:** turn the technology into a sustainable product without compromising portability or trust.  
**Dependencies:** P18–P24.  
**Deliverables:** commercial plan, approved license, working SaaS, billing and legal documentation.

**Tasks**

- [ ] **P25-T01** Validate the economic problem: current log prices, storage cost, queries and diagnostic effort.
- [ ] **P25-T02** Interview interested teams and quantify volume, requirements and willingness to pay.
- [ ] **P25-T03** Choose a license for the SDK/format/core and licenses for enterprise modules, with legal review.
- [ ] **P25-T04** Ensure the export and the open reader are enough for portability.
- [ ] **P25-T05** Define Community, Server/Enterprise and Cloud editions with clear limits.
- [ ] **P25-T06** Define billing units: GB ingested, GB stored, queries, retention or combinations.
- [ ] **P25-T07** Prototype pricing using real costs per customer profile; do not copy prices without measuring costs.
- [ ] **P25-T08** Implement auditable per-tenant usage metering and byte reconciliation.
- [ ] **P25-T09** Implement quotas, overage and warnings before blocking.
- [ ] **P25-T10** Integrate billing with the selected provider and a secure webhook/payment flow.
- [ ] **P25-T11** Create self-service onboarding: sign-up, project, token, installation, first event, first query.
- [ ] **P25-T12** Design multi-tenant isolation and dedicated deployment options for enterprise customers.
- [ ] **P25-T13** Prepare privacy policies, DPA, SLA, terms of service and data residency with professional advice.
- [ ] **P25-T14** Create a billing panel, subscription status, usage and budgets.
- [ ] **P25-T15** Build a license and update system for the Enterprise edition, without affecting reads of historical data.
- [ ] **P25-T16** Establish a sales process, demo, free trial and adoption feedback.
- [ ] **P25-T17** Run a private beta with real users, prioritize issues and measure conversion/retention.
- [ ] **P25-T18** Define exit clauses: complete export, verifiable deletion and portability.
- [ ] **P25-T19** Measure margins per cohort and high-egress/query scenarios.

**Gate Cloud GA:** controlled costs, solid identity and isolation, a tested payment flow, reviewed terms and working backups/DR.

### P26 — Product quality, chaos testing and internal certification

**Goal:** ensure reliable behaviour and repeatable results at the level of the whole product.  
**Dependencies:** every previous phase; part of the testing starts in P02.  
**Deliverables:** quality report, compatibility matrix, security and performance reports.

**Tasks**

- [ ] **P26-T01** Consolidate unit/integration/e2e coverage, prioritizing invariants and critical paths over a bare percentage.
- [ ] **P26-T02** Run property-based testing for codecs, schemas, adapters and query results.
- [ ] **P26-T03** Run fuzzing of the binary parser, HTTP, importers and MCP.
- [ ] **P26-T04** Run benchmarks with real and adversarial workloads and public datasets.
- [ ] **P26-T05** Verify integrity under segment corruption, cancelled compaction, upgrade and rollback.
- [ ] **P26-T06** Run long multi-threaded and multi-process stress on Linux/Windows.
- [ ] **P26-T07** Validate jitter, p99.9 and queueing latencies, not just average throughput.
- [ ] **P26-T08** Test recovery from `kill -9`, node loss, network failure, metadata rollback and a full disk.
- [ ] **P26-T09** Validate tenant isolation, RBAC, redaction, export and audit logs.
- [ ] **P26-T10** Verify interoperability across SDKs, readers, versions and operating systems.
- [ ] **P26-T11** Audit dependencies, SBOM, licenses and the supply chain.
- [ ] **P26-T12** Audit schema/file changes with automated backward-compatibility tests.
- [ ] **P26-T13** Run an external AI-friendly reading evaluation with known incidents and honest statistics.
- [ ] **P26-T14** Publish a methodological benchmark with enough conditions to reproduce the results.
- [ ] **P26-T15** Create performance budgets in CI that stop confirmed regressions.
- [ ] **P26-T16** Establish a list of blocking bugs (corruption, cross-tenant leak, false ACK, unrecoverable crash).
- [ ] **P26-T17** Close every blocker and document the limitations that are consciously accepted.

**Gate G26:** quality and security demonstrable under failure, with public benchmarks and no universal claims.

### P27 — Launch, documentation, adoption and support

**Goal:** ship an understandable, sustainable product that others can evaluate for themselves.  
**Dependencies:** P25–P26.  
**Deliverables:** site/documentation, public packages, changelog, demo, versioning policy and community.

**Tasks**

- [ ] **P27-T01** Finish a problem-oriented README: installation in minutes, local first and server second.
- [ ] **P27-T02** Publish a complete reference for the SDKs, the `.llog` format, the Query API, the CLI, MCP and configuration.
- [ ] **P27-T03** Publish a migration guide from Serilog, ILogger, Log4j, Pino and OTLP export.
- [ ] **P27-T04** Publish complete examples of a .NET backend, mixed microservices and local/cloud deployment.
- [ ] **P27-T05** Create a diagnostic demo: usual search vs a verifiable Context Pack, with explicit limits.
- [ ] **P27-T06** Publish benchmark results with hardware, datasets, versions and scripts.
- [ ] **P27-T07** Create documentation of codecs, algorithms, decisions and scientific limitations.
- [ ] **P27-T08** Publish the `CHANGELOG`, an upgrade guide, versioning and compatibility matrices.
- [ ] **P27-T09** Establish channels for issues, private vulnerability reports, discussions and support.
- [ ] **P27-T10** Create a PR review policy, contribution guide, code of conduct and mandatory tests.
- [ ] **P27-T11** Verify the name, domain, licenses and attributions before publishing.
- [ ] **P27-T12** Create a landing page with a concrete proposal based on verified metrics, avoiding promises of universal improvement.
- [ ] **P27-T13** Run a public release candidate, collect feedback and fix critical problems.
- [ ] **P27-T14** Publish a stable version and a launch-day rollback/support process.
- [ ] **P27-T15** Measure activation, ingested logs, queries, developer retention and onboarding failures.

**Gate Product GA:** installation and migration documented, open references, a tested product and defined operational support.

### P28 — Sustainable maintenance, evolution and new research

**Goal:** keep the product useful after the first launch.  
**Dependencies:** P27.  
**Deliverables:** quarterly/half-yearly roadmap, support policy, continuous changelog and a scientific backlog.

**Recurring tasks**

- [ ] **P28-T01** Keep supported versions of .NET, Java, Go, Python, Node and operating systems.
- [ ] **P28-T02** Run a regression benchmark on every relevant engine version.
- [ ] **P28-T03** Maintain a public corpus and sanitized datasets with controlled changes.
- [ ] **P28-T04** Update the comparative research on codecs and queries periodically.
- [ ] **P28-T05** Evaluate new codecs only through ablation experiments and with versioning support.
- [ ] **P28-T06** Keep historical files compatible according to the declared window.
- [ ] **P28-T07** Plan migrations and deprecations with notice and a working exporter.
- [ ] **P28-T08** Respond to CVEs and communicate severity/mitigation to users.
- [ ] **P28-T09** Adjust capacity and costs according to real usage.
- [ ] **P28-T10** Improve tools for external AI without putting models in the core.
- [ ] **P28-T11** Research context-aware queries, indexed correlations and even more compact reads.
- [ ] **P28-T12** Research new languages/architectures only after real demand.
- [ ] **P28-T13** Collect adoption metrics and prioritize verified problems.
- [ ] **P28-T14** Review license, security, privacy and legal obligations when entering new markets.
- [ ] **P28-T15** Publish the roadmap and the status of issues/compatibility transparently.

**Continuity gate:** every version is measurable, recoverable, compatible according to policy and sustainable.

---


## 3. Mandatory cross-cutting specifications

These rules apply to every phase, even when the specific phase comes much later.

### 3.1. Loss, durability and delivery contract

| Mode | What it means | Allowed risk | What must NOT be claimed |
|---|---|---|---|
| `Fast` | Event accepted into a local buffer | Loss if the process/machine fails | "persisted" |
| `Balanced` | Written in batches to the OS/file | Loss on failures before sync | "crash-durable" |
| `Durable` | `fsync`/persistence confirmed according to the contract | Physical failures and configuration still matter | "impossible to lose data" |
| `Remote` | ACK with explicit server semantics | Depends on spool and replication | "exactly-once" if not proven |

- Never report persistence success based only on in-memory queueing.
- Every loss through filters, sampling, saturation or retention must show up in counters/audit records.
- For apparent *exactly-once* at the consumer, evaluate idempotency with IDs and sequences; distinguish delivery from processing and from storage.
- Separate network reordering from business causality; a sequence of timestamps is not enough to infer causality.

### 3.2. The "lossless" contract

Two distinct profiles:

1. **Structured-exact:** typed values, schemas, identifiers, relationships and observable order, recovered unchanged according to the logical representation. Example: an NFC string vs the original bytes must be defined explicitly; never normalize silently.
2. **Raw byte-exact:** when importing text files, keep the original bytes, separators, encoding and order if faithful archiving mode is enabled.

The engine must not deduplicate by removing events. It may store a pattern + number of occurrences **only** if it keeps the data needed to reproduce the individual occurrences when the contract requires it.

### 3.3. The AI-friendly reading model

**There is no inference component.** The reader must be able to return:

- `summary`: intervals, counts and aggregated dimensions.
- `timeline`: an ordered sequence with local clock marks and explicit relationships.
- `evidence`: original events with stable IDs.
- `schema`: fields, declared meanings and version.
- `coverage`: filters, total, delivered, samples, gaps, errors and truncation.
- `provenance`: the event's origin, segment or logical identity, and source location if provided.

Default output must favour structure over prose. Prose is useful for people, but agents must be able to run reproducible queries and receive typed JSON.

**Contractual Context Pack example (conceptual):**

```json
{
  "format": "latticelog.evidence.v1",
  "scope": {"project": "inventory", "trace_id": "4df..."},
  "coverage": {
    "matched_events": 248,
    "returned_events": 7,
    "sampling": "deterministic-representatives",
    "truncated": true,
    "has_missing_segments": false
  },
  "timeline": [
    {"ref": "llog://p1/e/00851", "dt_us": 0, "event": "request.started"},
    {"ref": "llog://p1/e/00872", "dt_us": 14050, "event": "inventory.stock.timeout", "error_code": "STOCK_TIMEOUT"}
  ],
  "query": {"version": 1, "filter": "trace_id = '4df...'"}
}
```

`truncated=true` prevents the output from being presented as exhaustive. `llog://` is a conceptual reference scheme whose syntax and authorization must be specified before launch.

### 3.4. Concurrency and time semantics

- Store the wall-clock timestamp and, when possible, a local monotonic clock for durations.
- Design per-producer sequences and explicit correlation between processes.
- The arrival time at the server does NOT replace the time of occurrence.
- Inherited contexts must be immutable/versioned when encoding events.
- Distributed queries may need to merge segments with out-of-order data; state the limits and consistency windows.

### 3.5. Cross-cutting security

- Redaction before persistence when a field is known to be secret.
- Log entries, attributes, imported files and text returned through MCP are **untrusted data**.
- Authorization for export and search must match that of single reads.
- Never let clients run arbitrary queries outside their tenant, even with valid logical references.
- Use standard cryptography and audited libraries; **never invent encryption** as part of the algorithm.

### 3.6. Versioning policies

- **Format version** (`.llog`): a major break only with a well-defined migration/compatibility mode.
- **Schema version**: per event; keep historically used descriptors.
- **Codec version**: bitstreams state exactly which decoder they need.
- **Wire protocol version**: negotiated on connection.
- **Query API version**: independent of the binary format.
- **Evidence API version**: independent of the Query API and of persistence.
- **SDK version**: semver per language with a compatibility matrix.

---

## 4. Recommended monorepo structure

```text
latticelog/
├── README.md
├── LICENSE
├── ROADMAP.md
├── CHANGELOG.md
├── CONTRIBUTING.md
├── SECURITY.md
├── docs/
│   ├── product/                    # vision, PRD, user profiles
│   ├── architecture/               # C4, ADRs, flows
│   ├── decisions/                  # ADRs and phase reviews
│   ├── operations/                 # runbooks and DR
│   ├── security/                   # threat model, privacy
│   └── user-guides/                # SDKs, CLI, Query
├── specs/
│   ├── event-model.md
│   ├── llog-format-v0.md
│   ├── codec-contract.md
│   ├── wire-protocol.md
│   ├── query-language.md
│   └── evidence-protocol.md
├── src/
│   ├── LatticeLog.Abstractions/
│   ├── LatticeLog.Core/
│   ├── LatticeLog.Generator/
│   ├── LatticeLog.Storage/
│   │   ├── Format/
│   │   ├── Writer/
│   │   ├── Reader/
│   │   ├── Journal/
│   │   ├── Codecs/Structural/
│   │   ├── Codecs/Contextual/
│   │   ├── Codecs/Predictive/
│   │   ├── Compact/
│   │   └── Indexes/
│   ├── LatticeLog.Query/
│   ├── LatticeLog.Evidence/
│   ├── LatticeLog.OpenTelemetry/
│   ├── LatticeLog.Cli/
│   ├── LatticeLog.Transport/
│   ├── LatticeLog.Server/
│   ├── LatticeLog.Mcp/
│   └── LatticeLog.Web/
├── sdks/
│   ├── java/
│   ├── go/
│   ├── python/
│   └── node/
├── research/
│   ├── papers.md
│   ├── datasets/
│   ├── generators/
│   ├── baselines/
│   ├── experiments/
│   └── results/
├── benchmarks/
│   ├── Ingestion/
│   ├── Storage/
│   ├── Compression/
│   ├── Queries/
│   ├── AIReading/
│   └── Chaos/
├── tests/
│   ├── Unit/
│   ├── Integration/
│   ├── GoldenFiles/
│   ├── RoundTrip/
│   ├── PropertyBased/
│   ├── Fuzz/
│   ├── FaultInjection/
│   ├── Compatibility/
│   ├── Security/
│   └── EndToEnd/
├── examples/
│   ├── dotnet-api/
│   ├── worker/
│   ├── mixed-microservices/
│   └── local-cli/
├── deploy/
│   ├── docker/
│   ├── helm/
│   ├── terraform/
│   └── scripts/
├── tools/
│   └── generate-test-vectors/
└── .github/
    ├── workflows/
    ├── ISSUE_TEMPLATE/
    └── PULL_REQUEST_TEMPLATE.md
```

The final structure should be simplified where it helps: do not create empty projects ahead of time or split the solution prematurely.


## 5. Quality matrix and CI/CD from the start

| Pipeline | Trigger | Minimum validations | Blocks merge/release |
|---|---|---|---|
| Fast CI | Every PR | Formatting, build, unit, analyzers, contracts | Yes |
| Correctness CI | Every critical PR and nightly | Round trip, property tests, golden files | Yes |
| Security CI | Every PR and weekly | Secret scanning, SCA, SAST, SBOM | Yes on critical findings |
| Compatibility CI | Nightly/pre-release | N-1/N-2 format, SDKs, OS/arch | Yes for release |
| Fuzz CI | Nightly and continuous | Parser, importers, API, MCP | Yes on confirmed crashes |
| Benchmark CI | Nightly and release candidate | Latency, throughput, bytes, queries | Gate on confirmed regressions |
| Chaos CI | Pre-release | Crash, full disk, network, failover, upgrade | Yes |
| Documentation CI | Every PR | Links, snippets, compilable examples | Yes |
| Supply-chain release | Tag | Reproducible build where feasible, signing, artifacts, SBOM | Yes |

### Global Definition of Done

A task is **not** done without:

- [ ] Expected behaviour defined and edge cases documented.
- [ ] Code reviewed, typed/analyzed and with no known critical errors.
- [ ] Relevant unit and integration tests passing.
- [ ] Round-trip evidence for format or codec changes.
- [ ] CPU/RAM/IO/latency impact measured if it affects the hot path or storage.
- [ ] Security/PII assessed if it handles data input/output.
- [ ] Documentation and examples updated.
- [ ] Compatibility tests and historical files if a public contract changes.
- [ ] A link to the PR, CI results and ADR in the issue.
- [ ] Acceptance criteria approved with no hidden caveats.

### Absolute blocking criteria

- Silent data corruption or deletion in non-lossy mode.
- A "durable" ACK delivered before persistence according to the contract.
- Unauthorized cross-tenant access.
- Decoding that depends on state that was never persisted or cannot be recovered.
- Evidence output that does not report relevant truncation.
- A critical security regression or a known vulnerability without mitigation.
- Broken compatibility with supported files without an approved migration or deprecation.

---

## 6. Metrics, experiments and decision rules

### 6.1. Storage indicators

- `raw_bytes`: canonical input size under the comparison contract.
- `encoded_bytes`: size of the event streams.
- `dictionary_bytes`: catalogs and context tables.
- `index_bytes`: indexes, filters and locators.
- `metadata_bytes`: manifests, checkpoints, footers and the persistent journal.
- `temporary_bytes`: maximum temporary extra during compaction (report separately).
- `total_stored_bytes = encoded + dictionary + index + metadata`.
- `compression_ratio = raw_bytes / total_stored_bytes`.
- `savings_pct = 100 * (1 - total_stored_bytes / raw_bytes)`.
- `write_amplification = total_bytes_physically_written / logical_input_bytes`.

**Mandatory comparison:** the same dataset, equivalent preservation, the same order when it is part of the contract, the same indexing level reported separately, the same platform and measurement of the whole process. A format without indexes must not be proclaimed "better" than an indexed one if queries are needed: publish the results of both modes.

### 6.2. Performance indicators

- p50/p95/p99/p99.9 latency per logger call.
- Sustained events/s and bytes/s per producer, writer and server.
- Total CPU per million events, not just the producer thread's CPU.
- Allocations/event, GC pressure and RSS peaks.
- Compaction time and CPU; degradation during concurrent compaction.
- Durable ACK latency and post-crash recovery time.
- p50/p95/p99 latency of pattern queries and the bytes actually read.
- Horizontal scaling vs number of nodes, cost per TB and queries per CPU-hour.

### 6.3. Usefulness indicators for external readers/AI

- Tokens (real ones for the test model, or approximate ones stating the tokenizer).
- `evidence_recall`: relevant evidence found / known relevant evidence.
- `evidence_precision`: relevant evidence / evidence delivered.
- `answer_accuracy`: correct diagnosis on known-answer cases.
- `unsupported_claims`: claims without verifiable evidence.
- Number of queries, resolution time, network bytes and the cost of the external process.
- Percentage of results with `partial_results` represented correctly.

**Evaluation protocol:** the same model and external configuration, the same questions, controlled temperature where applicable, the same authorization, equivalent budgets, several attempts and blind human review of diagnoses on complex incidents. LatticeLog is not part of the inference process; it only delivers data.

### 6.4. Provisional thresholds (to be validated against baselines)

| Gate | Initial research goal | Decision if not met |
|---|---|---|
| Structural | Significant improvement vs plain binary for repetitive logs | Simplify the codec |
| Contextual | Positive net savings on several instrumented sets | Context only as a semantic index |
| Predictive | Net savings that justify its CPU/memory/query cost | Drop P, keep C |
| Local v1 | No corruption across the fault battery; stable SDK | Do not release |
| Evidence | Less irrelevant material with evidence recall no worse than the baseline | Revise the selection algorithm |
| Distributed | Correct behaviour under failover, quotas and partitions | Stay single-node only |
| Cloud GA | Acceptable margins and security in pilots | Do not launch a public SaaS (no longer applies) |

An aspirational target of **≥30 % fewer bytes than the best applicable baseline** and **≥50 % fewer tokens on certain tasks** can be used to steer R&D, but these numbers must not become absolute requirements before knowing what data volume and evidence quality are realistic. Also publish cases where the engine does worse.

### 6.5. Design of the five fundamental experiments

1. **Codec ablation:** raw → plain binary → S → C → P. Keep data and guarantees constant.
2. **Concurrency:** 1/2/4/8/16/32 producers with changing sizes and error proportions.
3. **Cardinality:** repetitive IDs vs UUIDs, hashes, random strings, large stack traces and high-entropy payloads.
4. **Selective queries:** error/trace/time range/field filter, hot and cold data, indexes on/off.
5. **External evidence:** the same incident, different representations, quality and tokens measured with no internal models.

---

## 7. Risks and pending architecture decisions

| ID | Risk or unknown | Impact | Mitigation strategy | Decision phase |
|---|---|---|---|---|
| R01 | Prediction increases size through metadata and state | High | Ablation, fallback, a negative gate is allowed | P09 |
| R02 | Contexts introduce a complicated dependency for random access | High | Snapshots/checkpoints, local index | P08–P11 |
| R03 | A codec compresses well but queries badly | High | A cost function that includes queries, selective indexes | P10–P11 |
| R04 | The SDK produces extreme latencies under saturation | Critical | Bounded queue, explicit policy, p99 test | P03–P05 |
| R05 | Crashes produce corruption/false ACKs | Critical | Journaling, atomic commits, fault injection | P05–P06 |
| R06 | Reordering causes false causal relationships | High | Explicit relationships and partial order | P01/P12 |
| R07 | The format becomes coupled to .NET | High | Public binary specification, golden vectors | P04/P21 |
| R08 | Predictor versioning prevents future reads | Critical | Persistent decoder/model version | P09/P14 |
| R09 | Logs contain secrets and PII | Critical | Redaction, RBAC, audit, retention | P01/P18 |
| R10 | MCP leads to massive unauthorized exposure | Critical | Read-only, budgets, auth, untrusted output | P22 |
| R11 | Global indexes end up larger than the data | High | Adaptive, compact, per-workload indexes | P11 |
| R12 | A distributed server makes operations too complex | High | Measure the single-node limit; shard only when needed | P19 |
| R13 | The SaaS costs more than it earns | High | Simulator and per-cohort pilots | P25 (no longer applies) |
| R14 | Incompatible codec/benchmark licenses | High | Audit before incorporating code | P02/P25 |
| R15 | Prior research covers the supposed novelty | High | Literature review and moderate claims | P02/P09 |
| R16 | The cluster fails under split-brain | Critical | Epoch/fencing/quorum, chaos | P19 |
| R17 | The design does not allow deleting specific data | High | Planned retention, crypto-erasure where applicable | P18/P20 |
| R18 | Exports give better diagnoses only through more context | Medium | Controlled token budget and evidence | P12 |
| R19 | Users do not accept format lock-in | High | Free decoder, exporters, documented format | P04/P13 |
| R20 | Performance promises that cannot be replicated | High | Transparent methodology, published hardware/config | P02/P26 |

### Decisions that must be recorded in ADRs

- `ADR-0001`: why .NET 10 LTS and not Rust/C++ at the start.
- `ADR-0002`: event and context contract.
- `ADR-0003`: `.llog` format, endianness and framing.
- `ADR-0004`: ACK semantics and journaling.
- `ADR-0005`: S/C/P criteria and fallback policy.
- `ADR-0006`: checkpoints and random access.
- `ADR-0007`: query language vs SQL/DSL.
- `ADR-0008`: `event_ref` stable under compaction.
- `ADR-0009`: remote protocol and deduplication.
- `ADR-0010`: shard/consistency model.
- `ADR-0011`: security of the external MCP.
- `ADR-0012`: open-source/commercial edition and export (decided: MIT, no commercial editions).

---

## 8. Phase-by-phase work organization

### 8.1. Issue template

```md
# [PXX-TYY] Title

## Goal
What behaviour/guarantee it delivers.

## Context and dependencies
Links to ADRs, specs and blocking issues.

## Scope
- [ ] Code change
- [ ] Validation
- [ ] Benchmarks (if applicable)
- [ ] Documentation

## Acceptance criteria
- [ ] Happy path validated
- [ ] Edge cases validated
- [ ] Errors/security validated
- [ ] Compatibility analyzed

## Evidence
PR, CI logs, benchmark CSV, golden file or reproducible test.

## Risks
What can fail; how to roll back.
```

### 8.2. Phase closing template

```md
# PHASE-XX-REVIEW

- Status: PASS | CONDITIONAL | FAIL
- Date and version/commit:
- PRs and issues closed:
- Verified deliverables:
- Test evidence:
- Benchmark evidence:
- Security/compatibility:
- Hypotheses accepted/refuted:
- Open limitations:
- New ADRs:
- Residual risks:
- Decision: proceed | repeat | simplify | stop
- Next phase authorized:
```

### 8.3. Branching, review and release strategy

- Use trunk-based development or short branches, avoiding research branches without reproducibility.
- Require CI and review for merges to the core and for format changes.
- Any binary change must include golden vectors, a version and a migration test.
- Codec PRs must include a comparative report against the baseline and adversarial cases.
- Evidence interface PRs must include a JSON example, the output budget and `coverage`.
- Releases with an `rc` and a stable channel; non-trivial migrations need a backout plan.

### 8.4. Recommended execution rhythm

- Short sprints of 1–2 weeks: pick unblocked tasks, implement, validate and close with evidence.
- A gate review at the end of each phase; do not pile up critical debt for "later".
- A monthly research review while P07–P10 are active.
- Security, format and observability reviews as continuous practices.
- Keep `ROADMAP.md` with `Current`, `Next`, `Later` and `Blocked`, and record the reasons for changes.

### 8.5. Team and capacity plan (indicative, not a contractual estimate)

| Milestone | Minimum useful roles | Notes |
|---|---|---|
| M0–M2 | 1 backend/systems engineer + occasional technical review | Only possible if research is bounded |
| M3 | 1–2 backend/systems + part-time QA/security | Local engine and packages |
| M4 | 2–4 backend/systems + DevOps/security | Server and auth |
| M5 | A larger team depending on real volume + SRE | Distributed and multi-tier storage |
| M6 | Backend, frontend, SRE, product, support and legal advice | SaaS and continuous operation (no longer applies) |

A single developer can make a lot of progress on the local engine; a distributed, secure enterprise SaaS usually needs more capacity for testing, operations and support. Scope must be adjusted to the evidence and resources available.

### 8.6. Priority order by value/risk

1. **Non-negotiable:** contract, fidelity, reader, durability, basic security, benchmarks.
2. **Differentiation:** S/C/P, selective queries, a Context Pack with coverage and references.
3. **Adoption:** OTel bridges, NuGet, CLI, documentation, interoperability.
4. **Scale:** server, permissions, replication, tiers, multi-SDK.
5. **Commercial:** console, cloud, billing, enterprise, support (billing/enterprise no longer apply).

Never sacrifice point 1 to build point 5 sooner.

---

## 9. Expected examples at the end of the project

### 9.1. .NET SDK (illustrative API at writing time)

```csharp
[LatticeEvent(
    Id = 2101,
    Name = "inventory.stock.reservation.failed",
    Level = LatticeLevel.Error)]
public static partial void StockReservationFailed(
    ILatticeLogger logger,
    long productId,
    int requested,
    int available,
    Exception? cause);

using var scope = logger.BeginOperation("ReserveStock", operationId);
AppEvents.StockReservationFailed(logger, productId, requested, available, ex);
```

### 9.2. Local commands (syntax proposed at writing time)

```bash
latticelog init ./logs
latticelog inspect ./logs
latticelog query ./logs --where "service='inventory' AND level>=ERROR" --since 1h
latticelog trace ./logs --trace-id 4df... --format json
latticelog context ./logs --trace-id 4df... --max-bytes 32000 --format json
latticelog verify ./logs
latticelog compact ./logs --policy compact
latticelog export ./logs --format jsonl --output logs.jsonl
```

### 9.3. Centralized flow (target architecture)

```text
Applications (.NET / Java / Go / Python / Node.js)
    ├─ typed capture and explicit context
    ├─ local buffer and optional spool
    └─ authenticated remote ingestion
                   ↓
          Collector / Ingestion
                   ↓
          Hot Append-only Storage
                   ↓
      Structural compaction (S/C/P)
                   ↓
       Indexes and Warm / Cold segments
                   ↓
              Query Engine
           ┌───────┼────────┐
           ↓       ↓        ↓
          CLI    HTTP API   Console
                    ↓
            Evidence Reader
                    ↓
              MCP (optional)
                    ↓
             External AI / IDE

      [There are NO models or inference in LatticeLog]
```

### 9.4. Publishing metrics (report format)

```text
Dataset: inventory-repeated-v4
Hardware: CPU, RAM, SSD, OS, runtime, filesystem, power mode
Input: N events; structured-exact type
Baseline: JSONL + Zstd level X, version Y
Lattice-S: total_bytes; encode_s; decode_s; query_p95
Lattice-C: total_bytes; encode_s; decode_s; query_p95
Lattice-P: total_bytes; encode_s; decode_s; query_p95
Adversarial: random data and uninstrumented records
Conclusion: conditions of gain and loss, confidence intervals
Verification: hashes and successful round-trip tests
```

---

## 10. Reference and research library (checked in November 2026)

These references support decisions; **their experimental results are not automatically achievable targets for LatticeLog**.

### Storage and capture

1. [NanoLog — A Nanosecond Scale Logging System (USENIX ATC 2018)](https://www.usenix.org/conference/atc18/presentation/yang-stephen) — moving work off the hot path into compile time/post-processing.
2. [CLP — Efficient and Scalable Search on Compressed Text Logs (OSDI 2021)](https://www.usenix.org/conference/osdi21/presentation/rodrigues) — lossless compression and search over compressed logs.
3. [μSlope — High Compression and Fast Search on Semi-Structured Logs (OSDI 2024)](https://www.usenix.org/conference/osdi24/technical-sessions) — specialized compression of semi-structured logs.
4. [LogCrisp — Fast Aggregated Analysis on Large-scale Compressed Logs (USENIX ATC 2025)](https://www.usenix.org/conference/atc25/presentation/wei) — vectorized queries and pattern extraction.
5. [Tracezip — Efficient Distributed Tracing via Trace Compression (ISSTA 2025)](https://conf.researchr.org/details/issta-2025/issta-2025-papers/19/Tracezip-Efficient-Distributed-Tracing-via-Trace-Compression) — span redundancy and retrieval structures.
6. [LogPrism — Unifying Structure and Variable Encoding for Effective Log Compression (arXiv, January 2026)](https://arxiv.org/abs/2601.17482) — correlations between structures and variable fields.

### Standards, SDKs and external access

7. [OpenTelemetry Logs Data Model](https://opentelemetry.io/docs/specs/otel/logs/data-model/) — semantic interoperability of events/logs.
8. [OpenTelemetry Semantic Conventions for Events](https://opentelemetry.io/docs/specs/semconv/general/events/) — event names and attributes.
9. [Model Context Protocol — Specification 2026-07-28](https://blog.modelcontextprotocol.io/posts/2026-07-28/) — the current specification at writing time; verify again when implementing.
10. [MCP — Server Tools Specification](https://github.com/modelcontextprotocol/modelcontextprotocol/blob/main/docs/specification/2026-07-28/server/tools.mdx) — the external tools interface.
11. [.NET Support Policy](https://dotnet.microsoft.com/en-us/platform/support/policy) — .NET 10 LTS as the initial base; check maintenance and patch dates while developing.

### Using these sources correctly

- Read the publication and the code when available, examine their datasets and check licenses before reusing any implementation.
- Reproduce the tests **on our hardware and under equivalent conditions** before drawing competitive conclusions.
- Record in `research/papers.md`: year, dataset, log type, method, advantages, limitations and reproduced/not reproduced results.
- Survey the scientific landscape again before claiming originality or drafting patent applications.

---

## 11. Initial checkpoint: first verifiable actions

Although this document covers the whole product, the **immediate work** should follow this sequence:

- [ ] Create the `latticelog` repository and add this Markdown as `docs/MASTER-PLAN.md`.
- [ ] Create `ROADMAP.md` with P00→P28 and open the 29 corresponding epics.
- [ ] Complete P00 and approve goals/non-goals.
- [ ] Complete P01 with types, schema, context, durability semantics and ordering.
- [ ] Build the P02 lab and freeze reproducible baselines.
- [ ] Develop P03–P06 to get a stable writer/reader before inventing complex encoders.
- [ ] Run P07, P08 and P09 as independent experiments with decision gates; do not assume P is better.
- [ ] Keep the AI-friendly reading goal of P12 as a product contract from P01, even though its implementation comes later.

**Intended end result:** an open and reversible format, a very efficient SDK, a local/centralized engine with selective queries, a system of verifiable evidence usable by any external AI and an operable platform; all without running artificial intelligence models inside LatticeLog.

**Master rule:** *innovation must be demonstrated with reproducible benchmarks, not with algorithm names or promises of universal compression.*
