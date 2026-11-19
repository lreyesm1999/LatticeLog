# License and project model (P25)

**Decided on 2026-11-18**: LatticeLog is free and open, under the **MIT** license for the whole repository. No paid
editions, SaaS or billing are planned.

## Why MIT

- It is what the owner asked for: a project that is free for everyone, like their other public repositories.
- It is the shortest and best-known license. Anyone can use, modify and redistribute the code, in commercial projects
  too, as long as the copyright notice is kept.
- It is compatible with every dependency (K4os.Compression.LZ4, ZstdSharp and BenchmarkDotNet are MIT; xUnit is
  Apache-2.0 and only used in tests).
- It protects the portability promise: the `.llog` format, its specification and the independent reader
  (`tools/pyllog`) can be reused without restrictions.

## Alternative considered

**Apache-2.0** adds an explicit patent grant and requires changes to be noted. It would be preferable if companies with
patents at stake contributed. For a free personal project, MIT is simpler.

## What the code already guarantees to its users

Complete lossless export (`GET /v1/export`, `latticelog export`), verifiable erasure (`/v1/admin/erase`, with its
limits regarding backups and replicas) and a documented format with an independent reader.
