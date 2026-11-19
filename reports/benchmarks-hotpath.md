# Producer hot path — BenchmarkDotNet (P03-T06, P03-T15)

Date: 2026-11-18 · `benchmarks/LatticeLog.Benchmarks` · BenchmarkDotNet 0.15.8, in-process toolchain, 5 warmup +
15 iterations · Intel Core i9-14900KF, Windows 11 10.0.26300, .NET 10.0.15 x64 RyuJIT (x86-64-v3).
Null sink (no I/O): measures producer + queue + writer thread (redaction included), not the disk.

| case | run 1 (before the redaction cache) | run 2 (after) | allocated/op |
|---|---:|---:|---:|
| typed event, level disabled | 1.79 ns | 1.29 ns | 0 B |
| MEL `LoggerMessage` disabled (reference) | 0.69 ns | 0.13 ns | 0 B |
| typed event enabled (4 fields) | 315 ns | 185 ns | 414–422 B |
| free text + 2 attributes enabled | 178 ns | 335 ns | 339–347 B |

Writer-thread counters during the run (closed loop, no rate limit):

| case | accepted | written | dropped by saturation |
|---|---:|---:|---:|
| typed, run 1 | 7.9 M | 3.9 M | 16.3 M |
| free text, run 1 | 12.1 M | 8.1 M | 12.0 M |
| typed, run 2 | 18.7 M | 14.7 M | 29.5 M |
| free text, run 2 | 48.2 M | 46.4 M | **0** |

Interpretation:
- **Disabled path**: 0 allocations, 1.3–1.8 ns. Meets the target (< 5 ns) but is slower than MEL (interface dispatch on
  `ILatticeLogger.IsEnabled`). Possible improvement: expose the minimum level as a field so the generated code compares
  without a virtual call.
- **Enabled path**: 185–335 ns and ~340–420 B per event (record + attribute array + queue nodes). Variance between runs
  is large because the result mixes the producer's cost with saturation dynamics: when the queue is full, dropping is
  cheaper than enqueuing; when the writer keeps up, objects live longer and are promoted to Gen1/Gen2. It does **not
  yet meet** a reasonable target of < 100 ns / < 200 B.
- **Finding and fix**: the writer thread compared every key against ~15 sensitive patterns on every record. A per-key
  cache (`RedactionPolicy.IsSensitiveKey`) removed the drops in the free-text case (0 of 48 M).
- Every loss was counted (`DroppedSaturation`); no drop was silent.

## P14 update (2026-11-18, same machine and configuration)

Three changes, measured one at a time with the same benchmark (`--filter '*Enabled*'`, null sink):

| step | typed (4 fields) | allocated | free text + 2 attributes | allocated |
|---|---:|---:|---:|---:|
| before (`ConcurrentQueue` + a `SortedSet` of tickets) | 189 ns | 417 B | 252 ns | 340 B |
| 1. preallocated bounded ring (`BoundedRing`, position = ticket) | 80 ns | 488 B | 68 ns | 280 B |
| 2. enumerator-free redaction (index loop + per-descriptor sensitivity cache) | 75 ns | 360 B | 67 ns | 280 B |
| 3. record pool (`LogRecordPool`) for generated events and the free-text API | **56 ns** | **5 B** | **63 ns** | **105 B** |

Final run with all 4 cases: disabled event 1.12 ns / 0 B; disabled MEL 0.12 ns; enabled typed 55.6 ns / 5 B (Gen0
0.0003 per 1000 operations); free text 63.1 ns / 105 B (the caller's `params` array).

An honest reading:
- Step 1 raised the measured allocations of the typed case (417 → 488 B) even though it more than halved the time:
  before, two thirds of the records were **dropped** by saturation and never reached the writer thread, so what the
  writer allocated was not fully counted. With the ring the writer no longer falls behind (**0 drops** in 193 M
  records) and its full cost shows up. That uncovered step 2: `RedactionPolicy.Apply` iterated an `IReadOnlyList` with
  `foreach`, one enumerator per attribute and record.
- The pool only recycles a record when the sink declares `IRecordSink.RetainsRecords = false` (`StoreWriter` does);
  any other sink or logger simply leaves records to the GC. Attribute arrays passed by the caller are never cleared or
  reused (test `Pooled_records_are_recycled_without_corrupting_persisted_data`).
- Target proposed in the previous version (< 100 ns / < 200 B on the enabled path): **met** in both cases.
- Limitations: one producer thread, null sink, one Windows machine. The cost with a real disk under sustained load is in
  `reports/latency-v1.md`. Open: Linux and ARM64.
