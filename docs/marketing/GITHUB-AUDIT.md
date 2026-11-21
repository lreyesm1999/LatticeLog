# GitHub launch audit

Audit of `lreyesm1999/LatticeLog` at commit `4b61529` (2026-11-21), before a public adoption campaign. It covers what a
developer who has never heard of the project sees, tries and has to trust. Findings marked **fixed** were addressed on
the branch `docs/launch-readiness`; **owner** means it needs the owner's account, money or a decision.

## Summary

LatticeLog is far more complete and more honest than most projects at launch: 15 libraries, a CLI with a Native AOT
build, an MCP server, a per-user service with a web console, a single-node server, Python and PHP SDKs, 232 tests on
four platforms, CodeQL, an SBOM, a container scan, seven pre-releases with checksummed executables, and measurement
reports that publish their own negative results. The engine is not the problem.

The problems were in **presentation and first contact**:

1. The README was 495 lines, mixed a landing page with an operations manual, and did not show a concrete result until
   line ~280.
2. The fastest real path (a prebuilt executable) was described as "build from source" in the Quick Start, while seven
   releases already had executables.
3. Some numbers were stated without the context the reports give (the 56 ns figure is a null-sink measurement; the
   latency report's multi-second stalls were not mentioned).
4. There was no ASP.NET Core example, no MCP how-to per client, no troubleshooting page and no first-contribution path.
5. Stale facts: 142 tests (actually 232), version `0.1.0-alpha.1` (releases are at alpha.7), CI verified golden files
   v0.1/v0.2 but not v0.3.

None of these needed changes to the engine.

## Findings

Scored 1 (low) to 3 (high). **Priority** = adoption impact + trust, weighed against effort and risk.

| # | Finding | Impact on adoption | Trust | Effort | Risk | Status |
|---|---|:-:|:-:|:-:|:-:|---|
| 1 | README: no result above the fold; 495 lines; operations detail before value | 3 | 2 | 2 | 1 | **fixed**: landing page with a real incident, details moved to `docs/` |
| 2 | Quick Start says "build from source" only; release executables not mentioned | 3 | 1 | 1 | 1 | **fixed** in README and `docs/guides/install.md` |
| 3 | No NuGet/PyPI/Packagist packages: integrating means a `ProjectReference` into a clone | 3 | 2 | 2 | 2 | **owner** (accounts, signing, versioning policy); release workflow can be extended once decided |
| 4 | Headline numbers without their conditions (null sink; one unstable machine; latency stalls) | 2 | 3 | 1 | 1 | **fixed**: README benchmark table states conditions; `docs/benchmarks.md` |
| 5 | Test count (142) and version (alpha.1) stale in README; MCP `serverInfo` hard-codes `0.1.0-alpha.1` | 1 | 2 | 1 | 1 | README **fixed**; version scheme → starter issue #8 (**owner** decision) |
| 6 | CI and `reproduce.*` verify golden files v0.1/v0.2 but not v0.3 (current format) | 1 | 3 | 1 | 1 | **fixed** (CI, scripts, compatibility doc) |
| 7 | No ASP.NET Core / `ILogger` example | 3 | 1 | 2 | 1 | **fixed**: `examples/aspnetcore` with `--smoke`, guide |
| 8 | No per-client MCP guide (Claude Code / Cursor scopes and file paths) | 3 | 1 | 1 | 1 | **fixed**: `docs/guides/mcp.md` (paths verified in `SourceCommands.cs` and with `--dry-run`) |
| 9 | README examples were not checked automatically | 2 | 3 | 1 | 1 | **fixed**: `scripts/check-examples.sh` + CI step (runs locally; first CI run pending) |
| 10 | No comparison with established tools | 2 | 2 | 1 | 2 | **fixed**: README table + `docs/comparison.md`, capabilities only, categories distinguished |
| 11 | No troubleshooting / migration guides | 2 | 1 | 1 | 1 | **fixed** |
| 12 | No `SUPPORT.md`, no question template, no first-contribution path, no starter issues | 2 | 2 | 1 | 1 | **fixed** (issues drafted, not opened: `good-first-issues.md`) |
| 13 | `SECURITY.md` promises acknowledgement in 3 business days and fixes in 30 days | 1 | 3 | 1 | 1 | **owner**: keep only if you can honour it alone; otherwise soften to "best effort" |
| 14 | No social preview image; no logo mark; console has no favicon | 2 | 1 | 1 | 1 | image and logo **fixed**; upload is **owner**; favicon → starter issue #2 |
| 15 | No real screenshots of the console | 2 | 1 | 1 | 1 | **fixed**: three real screenshots on clearly labelled demo data |
| 16 | Repo description is 186 characters; topics miss `csharp`, `opentelemetry`, `log-analysis`… | 2 | 1 | 1 | 1 | **owner**: values in `github-metadata.md` |
| 17 | Discussions off, Wiki on and empty | 1 | 1 | 1 | 1 | **owner** |
| 18 | Release notes are one generic sentence; CHANGELOG has only "Unreleased" across 7 tags | 2 | 2 | 2 | 1 | **owner** (needs a decision on how to cut entries per tag) |
| 19 | Golden-file READMEs are in Spanish while the rest is English | 1 | 1 | 1 | 1 | starter issue #1 |
| 20 | Prebuilt executables only for Windows x64 and Linux x64 (CI already tests macOS ARM64, Linux ARM64) | 2 | 1 | 2 | 2 | starter issue #5 |
| 21 | `ROADMAP.md` is a phase table (P00–P28) that outsiders cannot read quickly | 1 | 2 | 1 | 1 | **fixed**: plain-language summary on top, phases kept |
| 22 | Single maintainer; one GitHub star; no external users yet | 2 | 2 | – | – | not fixable by docs; stated openly in the comparison and launch drafts |
| 23 | `P14 Local v1.0: NOT PASSED`, `P18 security: NOT PASSED` in the roadmap | 2 | 3 | 3 | – | engine work; launch copy says "alpha" and lists these openly |

## What was verified

| Area | How | Result |
|---|---|---|
| Structure and features | read every top-level doc, the CLI help, the MCP tool list, specs index, examples, SDK READMEs | features in the docs exist in the code; nothing invented was found |
| Benchmarks | read all five reports and their methods; checked every number quoted in the README against them | numbers match; conditions were missing in the README (fixed) |
| Install | built the solution with the pinned SDK; ran the demo, the CLI investigation, the ASP.NET example, `mcp install --dry-run` for Claude Code and Cursor, the developer-flow MCP script | all worked; the release executables were **not** downloaded or run in this audit (CI's release job only checks `--version`) |
| Tests / CI | `dotnet test` locally (twice); last CI, CodeQL and release runs on GitHub | 232/232 on the second run; the first had one failure (`EvidenceAndInteropTests.Error_groups_and_markdown_escape_untrusted_text`, the `total_events` assertion) that passed 3/3 in isolation: possibly flaky, or this machine's known instability. CI green on `main` |
| SDKs | read READMEs and tests; ran the Python SDK through the developer-flow demo | Python: local and server modes. PHP: server shipping only (no local mode); tests skipped where PHP is missing |
| License and community files | read all | MIT; Code of Conduct (Contributor Covenant 2.1 by reference), CONTRIBUTING, SECURITY, CODEOWNERS, issue and PR templates present |

## Claims that were not backed and how they were handled

| Claim (before) | Problem | Now |
|---|---|---|
| "56 ns per event on the hot path" (README card) | null sink, one producer thread: it measures the producer, not the disk | stated with "null sink, one thread" and a link; latency report summarized with its stalls |
| "142 automated tests" | outdated | 232 |
| "Like Redis or Ollama" (install section) | analogy, harmless | kept in `docs/guides/install.md`, not on the landing page |
| "20–40× faster" | true only when time/severity indexes prune, vs a full `.llog` scan (not vs other tools) | stated as such |

No benchmark was re-run or altered.

## Remaining blockers before a broad public launch

1. **Packages.** A .NET developer will not integrate a library through a `ProjectReference` to a clone. Publishing to
   NuGet (even as `-alpha`) is the single highest-impact change. Needs: a nuget.org account and API key in repository
   secrets, a decision on package IDs and signing, and a `pack`+`push` job in `release.yml`.
2. **Release notes and changelog per version**, so "what changed in alpha.7" has an answer.
3. **One run of the benchmarks on other hardware** (ideally Linux), so the headline numbers do not rest on a machine
   the reports themselves call unstable.
4. **Owner settings**: description, topics, social preview, Discussions (see `github-metadata.md`).

See [`launch/README.md`](launch/README.md) for the launch sequence and checklist.
