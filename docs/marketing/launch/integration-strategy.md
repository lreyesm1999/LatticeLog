# Integration strategy: contributing LatticeLog where it genuinely helps

Status: **plan, nothing executed.** No projects have been searched, contacted or forked. This document describes how to
find open-source projects where LatticeLog could solve a real problem, and how to propose it without being a nuisance.

The goal is a handful of useful, well-received integrations, not reach. One merged integration that a maintainer is
happy with is worth more than any number of opened PRs.

## Readiness gates before any outreach

Do not approach another project until these hold. A PR into someone else's repository cannot depend on a
`ProjectReference` to a clone of LatticeLog.

- [ ] **Packages published**: `LatticeLog.Core`, `LatticeLog.Generator` and `LatticeLog.Extensions.Logging` on NuGet
      (and, for Python projects, the SDK on PyPI). Signed, with a stable version scheme.
- [ ] **A non-alpha (or at least beta) release** with the local v1.0 gate (P14 in `ROADMAP.md`) passed, or an explicit
      statement to the maintainer that it is alpha and why that's acceptable for an opt-in sink.
- [ ] **Benchmarks repeated on stable hardware**, ideally Linux, so numbers quoted to a maintainer aren't from a machine
      with known CPU instability.
- [ ] **An upgrade and compatibility policy** a maintainer can read (`docs/compatibility.md`,
      `docs/release/maintenance-policy.md`) that is actually being followed.
- [ ] **Capacity to support it**: someone available to answer issues in the other project for months. An abandoned
      integration costs the other maintainer more than it ever helped.

Until then, the only acceptable form of outreach is asking questions and learning what people need.

## Selection criteria

A candidate project should meet most of these:

| criterion | why it matters | how to check |
|---|---|---|
| .NET application or tool, on .NET 8+ (ideally 10) | The typed-event path and the `ILogger` provider are .NET | `global.json`, `TargetFramework` |
| Already uses `Microsoft.Extensions.Logging` | LatticeLog can be added as one more provider with no code changes | `ILogger<T>` usage, `AddLogging` |
| Runs locally or at the edge: CLI tools, desktop apps, dev tools, self-hosted services, IoT | Local-first storage and crash recovery are the strongest fit | README, deployment docs |
| Diagnostics are hard for its users | Issue templates asking for logs, "please attach your log file" in issues | issue tracker |
| Has open issues about logs: size, rotation, structured output, finding errors | A concrete, stated problem to solve | issue search below |
| MCP server or agent tooling, in .NET or Python | Logs by name over MCP, coverage, untrusted-content marking | topics `mcp`, `model-context-protocol` |
| Active maintainers, contribution guide, recent merges | A PR will be reviewed | commit and PR history |
| Permissive or compatible license | MIT dependency must be acceptable | `LICENSE` |

Exclude projects that already have a logging pipeline they're happy with (Serilog with Seq, OpenTelemetry to a
backend) unless an issue says otherwise. Exclude projects whose contribution guide rejects new dependencies.

## Search queries (to run manually)

GitHub code and issue search, run by a person, results reviewed by hand:

```text
# .NET projects with open logging problems
is:issue is:open language:C# "log file" (large OR size OR rotation)
is:issue is:open language:C# "structured logging" label:enhancement
is:issue is:open language:C# "attach the log" OR "attach your log"
is:issue is:open language:C# "logs" "hard to find" OR "hard to read"

# .NET apps that run locally and use ILogger
language:C# "ILogger<" "WinForms" OR "Avalonia" OR "MAUI" OR "WPF" in:file
topic:dotnet-tool topic:cli language:C#
topic:avalonia OR topic:maui stars:>100 pushed:>2026-06-01

# MCP servers and agent tooling
topic:mcp language:C#
topic:model-context-protocol language:Python "logging"
is:issue is:open "mcp" "logs" (debug OR diagnose)

# Python services already on structlog (local mode with one processor)
"structlog.configure" language:Python stars:>200 pushed:>2026-06-01
```

Record each candidate in a private spreadsheet, not in a public issue.

## Scoring rubric

Score each candidate 0–2 per row; pursue only projects scoring 9 or more of 14.

| | 0 | 1 | 2 |
|---|---|---|---|
| Stated problem | none found | indirect (users struggle with logs) | open issue asking for what LatticeLog does |
| Technical fit | no `ILogger` / no .NET or Python | `ILogger` but server-side with existing backend | local app or tool on `ILogger` or structlog |
| Maintainer activity | no merges in 6 months | occasional | regular reviews and merges |
| Openness to dependencies | guide forbids them | unclear | has optional integrations or plugins |
| Size of change | invasive | moderate | opt-in, a few lines, off by default |
| Measurable benefit | can't measure | qualitative only | size, query time or diagnosis steps measurable on their data |
| Our ability to support it | no | for a release or two | long term |

## How to demonstrate benefit

1. **Reproduce their problem first**, using their project and their kind of logs. If the issue is "log files are huge",
   generate realistic logs with their own code paths.
2. **Measure before and after** with the same data: bytes on disk, time to answer the question in the issue, records
   lost after a forced kill. Use LatticeLog's reports as the method template; publish the raw numbers and the commands.
3. **Report the losing cases too.** If zstd on their text logs is smaller, say so.
4. **Keep it opt-in.** The integration is an extra provider or sink enabled by configuration. No default changes, no
   removal of their existing logging, no new mandatory dependency in their core package if a plugin package is possible.
5. **Show the user-facing result**: the command a user runs to get the answer they couldn't get before, with real
   output.

## How to approach maintainers

1. **Open an issue or discussion first**, never a cold PR. Describe their problem in their words, link the issue that
   states it, and ask whether an opt-in integration would be welcome. Include the measurements.
2. **Disclose authorship in the first sentence**: "I maintain LatticeLog, so I'm biased."
3. **Ask about their constraints**: dependency policy, supported runtimes, release cadence.
4. **Accept "no" or silence** as an answer. Don't re-ask, don't ping repeatedly, don't move the pitch to another
   channel.
5. If they say yes, **follow their contribution guide exactly**: their style, their tests, their changelog format.
   Keep the PR small and reviewable.
6. **Stay for the follow-up**: answer issues about the integration in their tracker, and fix breakages caused by
   LatticeLog releases quickly.

## Anti-patterns (never do these)

- Mass outreach: the same message to many projects, or contacting projects that didn't state a matching problem.
- Drive-by promotional PRs: adding LatticeLog to a README, a "related projects" list, or as a default dependency.
- Automated or templated comments on issues, especially "have you tried LatticeLog?" replies.
- Opening PRs before the readiness gates above are met.
- Claiming results on their data that weren't measured on their data.
- Hiding the relationship between the contributor and LatticeLog.
- Using alternate accounts or asking others to upvote, star or comment.

## Tracking

For each approached project, keep: link, date of first contact, the problem quoted, measurements shared, the
maintainer's answer, and the outcome. Review after the first three: if maintainers consistently say no for the same
reason, fix that reason in LatticeLog before contacting anyone else.
