# PHASE-14-REVIEW — Production-ready .NET SDK (Local v1.0)

- Status: **NOT PASSED** (substantial progress; the remaining criteria need an RC with signed packages and N-1
  compatibility; ARM64 now covered in CI; MIT license decided)
- Date and version: 2026-11-18 · on top of `a10d1f9` (format 0.2) + the changes of this review
- PRs and issues closed: none (tasks are tracked by ID in docs/MASTER-PLAN.md)

## Done and verified in this review

| criterion | evidence | result |
|---|---|---|
| Durability proven with a real crash | `tests/LatticeLog.Tests/CrashTests.cs` + `tests/LatticeLog.CrashHarness` | 3 modes × 3 kills with rotation: 0 acknowledged records lost, a prefix with no gaps or duplicates, clean recovery |
| Catalogs of open segments | `DurabilityTests.Torn_tail_never_orphans_records_from_their_catalog`, ADR-0006 | a cut never leaves records without their catalog; residual risk bounded and documented |
| Allocations on the enabled path | `reports/benchmarks-hotpath.md` | typed 56 ns / 5 B (previously 189 ns / 417 B); free text 63 ns / 105 B |
| Ring concurrency | `LoggerTests.Ring_with_many_producers_...`, `Pooled_records_...` | 8 producers on a 16-slot ring: each record exactly once and in order per producer; recycling corrupts neither data nor redacted secrets |
| Fixed-rate latency | `benchmarks/LatticeLog.Benchmarks/FixedRate.cs`, `reports/latency-v1.md` | measured; open finding about writer-thread stalls (see below) |
| Trimming/AOT | `Directory.Build.targets`, trimmed CLI publish | analysis active and warning-free; trimmed CLI tested end to end. Native AOT verified in CI (Linux) |
| Packages | `dotnet pack` → 9 packages + snupkg; consumed from a clean project | generator bundled in `LatticeLog.Core`; typed events work from the package |
| Public data | Loghub-2.0, `reports/loghub2-v1.md` | byte-exact text import verified on 14 datasets |

Tests at the time: 103 on Windows x64 (all green, Debug and Release) + the independent Python reader over the v0.1 and
v0.2 golden files. Since publishing, CI runs the suite on Linux x64, Linux ARM64, macOS ARM64 and Windows x64.

## Open for PASS

- ~~ARM64 coverage~~ done: the full suite runs on Linux ARM64 (`ubuntu-24.04-arm`) and macOS ARM64 in CI.
- Writer-thread stalls: **root cause identified** (storage latency, fsync up to 5.3 s on an SSD at 98 % capacity, plus
  NTFS waiting when writing to a file being flushed) and **mitigated** with background durability (1 producer at
  500k/s: from 4/12 to 0/12 runs with a stall). With 4 producers they persist when the disk takes seconds. To be
  repeated on a disk with free space and on Linux, and to decide whether to split encoding and I/O.
- ~~License~~ (MIT); package signing, SourceLink to the public repository.
- N-1 compatibility and a release candidate.
- Power-loss / OS-crash tests (need a VM or fault-injection hardware).

- Residual risks: R04, R05; new: writer I/O stalls propagated to producers with the `Block` policy.
- Decision: do not release v1.0.
