# LinkedIn and X drafts

Status: **drafts, not posted.** Each post is tied to one real feature and links to the file that shows it. Links use
`https://github.com/lreyesm1999/LatticeLog/blob/main/...`; check that each target exists on `main` before posting
(the `docs/guides/` pages are new in the launch-readiness branch).

Rules: no hype words, no claim without a linked source, mention "alpha" whenever someone could reasonably assume
production readiness, and never imply LatticeLog runs AI.

## LinkedIn

### 1. Launch / introduction

> I've open-sourced LatticeLog, a local-first log and event engine for .NET 10, with Python and PHP SDKs (MIT, alpha).
>
> The idea: logs that keep their types, live in an open binary format on your own disk, and answer questions with an
> explicit statement of what was covered.
>
> • Events declared once; a source generator writes the logging code. A long stays a long.
> • .llog segments with per-block CRCs, crash recovery, and an index that lets queries skip what can't match.
> • Every answer reports coverage: matched, returned, truncated, exhaustive yes/no.
> • A read-only MCP server so coding agents can ask about a service by name. LatticeLog itself runs no AI.
>
> What it isn't: a hosted platform, a cluster, or production-hardened yet. Packages aren't on NuGet; there are prebuilt
> executables for Windows and Linux on GitHub Releases.
>
> Five-minute demo with an injected incident:
> https://github.com/lreyesm1999/LatticeLog/blob/main/docs/guides/getting-started.md
>
> Feedback on the design is very welcome.

### 2. Publishing negative results

> Benchmarks usually show where a project wins. The LatticeLog reports also show where it loses:
>
> • Typed events from 14 public Loghub-2.0 datasets: 25 % smaller than the same content as CSV + zstd-3, but 17 %
>   larger than CSV + zstd-19.
> • Storing raw log lines byte-exact: 83 % larger than zstd-3 on the same text.
> • A predictive codec was estimated before being built; the best case was 0.6 %, so it wasn't built.
> • All numbers come from one machine, and that machine's CPU is unstable under all-core load, so concurrency runs were
>   pinned to 4 cores.
>
> If you're evaluating a tool, the losing cases tell you more than the winning ones.
>
> https://github.com/lreyesm1999/LatticeLog/blob/main/reports/loghub2-v1.md

### 3. Logs for coding agents

> When a coding agent reads your logs, two things matter that grep doesn't give you: whether the answer is complete,
> and whether the log text is being treated as data.
>
> LatticeLog's MCP server answers by service name, groups errors with deterministic fingerprints, returns `llog://`
> references the agent can resolve to check a claim, and attaches a coverage section to every result. Fields holding
> log text are listed as untrusted.
>
> It works with Claude Code, Claude Desktop, Cursor and VS Code (`latticelog mcp install --client ...`). Nothing leaves
> the machine, and LatticeLog performs no inference.
>
> Setup guide: https://github.com/lreyesm1999/LatticeLog/blob/main/docs/guides/mcp.md

## X (each ≤ 280 characters)

1. Launch (≈250 chars)

   > LatticeLog: typed, local-first logs for .NET in an open binary format, with indexed queries and a read-only MCP
   > server for coding agents. MIT, alpha. 5-minute demo with an injected incident:
   > https://github.com/lreyesm1999/LatticeLog/blob/main/docs/guides/getting-started.md

2. Hot path (≈240 chars)

   > 189 ns → 56 ns and 417 B → 5 B per typed log event in .NET: a bounded ring, enumerator-free redaction, a record
   > pool. Null sink, one thread, one machine. The ring made allocations go up first; the report explains why.
   > https://github.com/lreyesm1999/LatticeLog/blob/main/reports/benchmarks-hotpath.md

3. Coverage (≈230 chars)

   > Every LatticeLog answer says what it covered: matched, returned, truncated, partial, exhaustive yes/no. An agent
   > can tell "no errors" from "didn't look". `latticelog errors ./logs`
   > https://github.com/lreyesm1999/LatticeLog#readme

4. Negative result (≈235 chars)

   > Negative result, kept in the repo: storing raw log lines byte-exact in .llog is 83 % larger than zstd-3. Typed
   > events from the same 14 Loghub-2.0 datasets are 25 % smaller than CSV+zstd-3, 17 % larger than CSV+zstd-19.
   > https://github.com/lreyesm1999/LatticeLog/blob/main/reports/loghub2-v1.md

5. Open format (≈225 chars)

   > The .llog format has a written spec and a second reader in Python with no shared code. CI makes it decode frozen
   > golden files, including corrupt ones, on every run.
   > https://github.com/lreyesm1999/LatticeLog/blob/main/specs/llog-format-v0.md

Count characters with the final URLs before posting (X counts every link as 23 characters).
