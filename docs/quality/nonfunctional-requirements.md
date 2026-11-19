# Non-functional requirements and target SLOs (P00-T05)

**All values are initial targets, not results.** Real measurements, with their hardware, are in `reports/`.

| area | initial target | how it is measured | status |
|---|---|---|---|
| Disabled logging (filtered level) | 0 allocations, < 5 ns per call | BenchmarkDotNet `HotPathBenchmarks` | measured, see `reports/benchmarks-hotpath.md` |
| Enabled typed event (producer path) | p99 < 2 µs without saturation; bounded, documented allocations | BenchmarkDotNet + latency test | partially measured |
| Sustained local throughput | ≥ 100k events/s per process in Balanced mode on desktop hardware | sample app / lab | 128k ev/s measured in one run (Windows x64) |
| Durable | p99 ACK < 10 ms on NVMe | pending | not measured |
| Crash recovery | no corruption and no invented records in 100 % of test cases | fault injection | met in tests (offset sampling) |
| Sequential read | ≥ 1M records/s decoded | lab | see `reports/baseline-v1.md` |
| Selective query | trace lookup without decoding segments the bloom filter excludes | `QueryTests`, lab | met |
| Server availability | 99.9 % monthly | P24 SLIs (`docs/operations/runbook.md`) | defined, not measured in production |
| Server durability | ACK only after persistence according to policy | P16 | met in tests |
| Security | no known secret persisted; log text marked as untrusted in outputs | tests | met for the local engine |
| Portability | file readable without .NET | independent Python reader | met (golden files) |
