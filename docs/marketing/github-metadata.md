# GitHub metadata to set by hand

These settings live on GitHub, not in the repository, so they need the owner's account. "Current" values were read
with `gh repo view` on 2026-11-21.

**Applied on 2026-11-21:** description, website, the 8 added topics, Discussions on (default categories, including Q&A
and Ideas, linked from the issue chooser and `SUPPORT.md`), Wiki off (it had no pages), and the labels below. Private
vulnerability reporting was already enabled. **Still to do by hand:** the social preview upload, the author profile, and
the release notes.

## Description (About → Description)

Current (186 characters, cut off in search results and cards):

> Typed, local-first logs in an open binary format (.llog) for .NET, with indexed queries, verifiable evidence for
> external AI, a single-node server and Python/PHP SDKs.

Proposed (≤ 120 characters, the words people search for first):

> Typed, local-first structured logging for .NET: an open binary log format, indexed queries and an MCP server for agents.

```bash
gh repo edit lreyesm1999/LatticeLog --description "Typed, local-first structured logging for .NET: an open binary log format, indexed queries and an MCP server for agents."
```

## Website (About → Website)

Empty today. Until there is a site, point it at the tutorial:
`https://github.com/lreyesm1999/LatticeLog/blob/main/docs/guides/getting-started.md`.

## Topics (About → Topics)

Current: `binary-format`, `dotnet`, `logging`, `mcp`, `observability`, `php`, `python`, `structured-logging`.

| Topic | Keep / add | Why it is accurate |
|---|---|---|
| `dotnet`, `csharp` | keep / **add** | .NET 10 library, C# source generator |
| `logging`, `structured-logging` | keep | core purpose |
| `log-analysis` | **add** | `errors`, `stats`, `trace`, context packs |
| `log-compression` | **add** | Lattice-S structural encoding; measured in `reports/` |
| `binary-format` | keep | the `.llog` spec |
| `mcp`, `model-context-protocol` | keep / **add** | built-in read-only MCP server |
| `developer-tools` | **add** | CLI, local console, editor integration |
| `local-first` | **add** | no server needed |
| `source-generator` | **add** | `[LatticeEvent]` Roslyn generator |
| `opentelemetry` | **add** | OTLP/JSON import and export (`src/LatticeLog.OpenTelemetry`) |
| `observability` | keep, with care | true for logs; there are no metrics or tracing backend |
| `python`, `php` | keep | SDKs exist |

GitHub allows 20 topics; this list has 16.

```bash
gh repo edit lreyesm1999/LatticeLog --add-topic csharp,log-analysis,log-compression,model-context-protocol,developer-tools,local-first,source-generator,opentelemetry
```

Not recommended: `ai`, `llm` (LatticeLog runs no model; it would attract the wrong expectations), `apm`, `tracing`,
`metrics`, `elasticsearch-alternative` (not what it does).

## Social preview (Settings → General → Social preview)

Upload [`docs/assets/social-preview.png`](../assets/social-preview.png) (1280×640, PNG). It is rendered from
[`social-preview.svg`](../assets/social-preview.svg) by `pwsh scripts/render-assets.ps1`; edit the SVG and re-render.
GitHub does not read it from the repository: it must be uploaded in the web UI.

## Features

| Setting | Today | Suggestion |
|---|---|---|
| Discussions | off | **Turn on** with Q&A and Ideas categories, then add a contact link in `.github/ISSUE_TEMPLATE/config.yml`. Keeps questions out of the issue tracker |
| Wiki | on, empty | Turn off; docs live in `docs/` where they are versioned and reviewed |
| Issues | on | keep |
| Private vulnerability reporting | linked from `SECURITY.md` | Check it is enabled (Settings → Code security) |

## Labels

Existing: `bug`, `documentation`, `duplicate`, `enhancement`, `good first issue`, `help wanted`, `invalid`,
`question`, `wontfix`, `accessibility`, `dependencies`, `github_actions`, `.NET`.

Suggested additions:

```bash
gh label create data-integrity --color B60205 --description "Lost, duplicated or corrupted records, or a false durability ACK"
gh label create "area: storage" --color 1D76DB --description ".llog format, recovery, compaction, retention"
gh label create "area: cli"     --color 1D76DB --description "latticelog command line"
gh label create "area: mcp"     --color 1D76DB --description "MCP server and per-user service"
gh label create "area: console" --color 1D76DB --description "Web console"
gh label create "area: sdk-python" --color 1D76DB --description "Python SDK"
gh label create "area: sdk-php"    --color 1D76DB --description "PHP SDK"
gh label create "area: release" --color 1D76DB --description "Release workflow, executables, update"
gh label create benchmarks --color 5319E7 --description "Measurements and reports"
```

## Releases

The latest release is a pre-release (`v0.1.0-alpha.7`) whose notes are one generic sentence. Before promoting the
project, edit the latest release notes to list what changed (from `CHANGELOG.md`) and link the tutorial. Consider
moving the `CHANGELOG.md` "Unreleased" entries under the versions that shipped them (`git log v0.1.0-alpha.N..`).

## Author profile

- **Pin** the repository on the profile.
- A profile README (`lreyesm1999/lreyesm1999`) with one line about LatticeLog and a link to the tutorial helps people
  who arrive from a post.
- Use the same one-sentence description everywhere (repo, profile, posts).

## Keywords already used in the README and docs

`structured logging`, `.NET`, `typed events`, `source generator`, `open binary format`, `indexed queries`,
`MCP server`, `coding agents`, `Claude Code`, `Cursor`, `ASP.NET Core`, `ILogger`, `OpenTelemetry (OTLP)`,
`log compression`, `local-first`. Headings use the terms people search for ("ASP.NET Core and ILogger", "MCP: give
Claude Code, Cursor… your logs", "Migrating from text logs").
