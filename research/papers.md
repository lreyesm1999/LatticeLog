# Research references (P02, master plan §10)

Review status: **not read or reproduced** in this repository; the URLs come from the master plan and must be
verified. No result from these publications is used as a LatticeLog target.

| # | work | year | relevant idea | reproduced | notes |
|---|---|---|---|---|---|
| 1 | NanoLog (USENIX ATC) | 2018 | move formatting to compile time / post-processing | no | inspires the typed event generator |
| 2 | CLP (OSDI) | 2021 | lossless compression with search over compressed logs | no | baseline candidate (P02-T10), check the license |
| 3 | μSlope (OSDI) | 2024 | compression of semi-structured logs | no | |
| 4 | LogCrisp (USENIX ATC) | 2025 | vectorized queries and patterns | no | |
| 5 | Tracezip (ISSTA) | 2025 | span redundancy | no | relevant to Lattice-C |
| 6 | LogPrism (arXiv 2601.17482) | 2026 | structure/variable correlation | no | |
| 7 | OpenTelemetry Logs Data Model | — | semantic model | applied | `specs/otel-mapping.md` |
| 8 | OTel semantic conventions for events | — | names/attributes | partially | `exception.*`, `service.*` |
| 9–10 | Model Context Protocol 2026-07-28 / tools | 2026 | tool interface | **not verified** | the adapter negotiates 2025-xx; update (P22-T01) |
| 11 | .NET support policy | — | .NET 10 LTS | applied | ADR-0001 |

Before claiming originality or drafting patents: a full literature review (R15).
