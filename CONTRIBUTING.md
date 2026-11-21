# Contributing to LatticeLog

Thanks for your interest. LatticeLog is a free, MIT-licensed project. Issues, questions and pull requests are all
welcome, and small ones are the best way to start.

## Your first contribution

1. **Run it once.** Follow [Getting started](docs/guides/getting-started.md) (about five minutes). If anything in it
   is wrong or unclear, that is already a useful issue or pull request.
2. **Pick something small.** Issues labelled [`good first issue`](https://github.com/lreyesm1999/LatticeLog/labels/good%20first%20issue)
   are scoped to a few files and say how to verify the change. Comment on the issue before starting, so two people do
   not do the same work.
3. **Build and test locally** (below), make the change, open a pull request that links the issue.

Good first areas that need no knowledge of the storage engine: documentation and guides, examples, CLI help texts and
error messages, the Python and PHP SDKs, and the web console (`src/LatticeLog.Server/ui`, plain HTML/CSS/JS).

## Before you start

- **Bugs**: open an issue with the version, OS, a minimal reproduction and what you expected. If the bug touches data
  integrity (corruption, lost or duplicated records, a false durability ACK), say so in the title. Those are treated as
  blockers.
- **Larger changes**: open an issue first so the design can be discussed before code is written.
- **Security issues**: never in a public issue. See [`SECURITY.md`](SECURITY.md).

## Local development

You need the .NET SDK pinned in [`global.json`](global.json), Git, and Python 3.10+ (for the independent format
reader). PHP 8.1+ and `structlog` are optional: the SDK tests that need them are skipped when they are missing.

```bash
git clone https://github.com/lreyesm1999/LatticeLog.git
cd LatticeLog
dotnet build LatticeLog.sln -c Release
```

| Area | Where | Notes |
|---|---|---|
| Logger, generator | `src/LatticeLog.Core`, `src/LatticeLog.Generator` | hot path: benchmark before and after |
| Format and storage | `src/LatticeLog.Storage`, `specs/llog-format-v0.md` | see "Changes with extra requirements" |
| Queries and evidence | `src/LatticeLog.Query`, `src/LatticeLog.Evidence` | |
| CLI | `src/LatticeLog.Cli` | ships as Native AOT: keep it trimming/AOT-compatible (warnings are errors in `src/`) |
| Sources, MCP, service | `src/LatticeLog.Sources`, `src/LatticeLog.Mcp`, `src/LatticeLog.Service` | |
| Server and console | `src/LatticeLog.Server` (`ui/` is the web console) | |
| SDKs | `sdks/python`, `sdks/php` | standard library only |

Run the CLI from source with `dotnet run -c Release --project src/LatticeLog.Cli -- <command>`. To keep your real
LatticeLog configuration untouched while testing sources, MCP or the service, set `LATTICELOG_CONFIG_DIR` and
`LATTICELOG_DATA_DIR` to scratch folders.

## Running the tests

```bash
dotnet test LatticeLog.sln -c Release                                    # all .NET tests (~230)
dotnet test LatticeLog.sln -c Release --filter "FullyQualifiedName~QueryTests"   # one class
python -m unittest discover -s sdks/python/tests                         # Python SDK (needs structlog)
python -I tools/pyllog/llog.py verify-golden tests/GoldenFiles/v0.1 tests/GoldenFiles/v0.2 tests/GoldenFiles/v0.3
sh scripts/check-examples.sh                                             # the commands the docs show
dotnet format whitespace LatticeLog.sln --verify-no-changes              # formatting, as CI checks it
```

CI runs all of the above on Windows x64, Linux x64, Linux ARM64 and macOS ARM64, plus a Native AOT build, the server's
container image with an HTTPS smoke test, a cross-OS backup restore and CodeQL
([`.github/workflows/ci.yml`](.github/workflows/ci.yml)).

If tests crash randomly under heavy parallel load on your machine, retry with fewer CPUs before suspecting the code;
see [Troubleshooting](docs/guides/troubleshooting.md#building).

## Pull requests

- Keep each PR to one concern, branched from `main`. CI must be green on every platform.
- Add or update tests with every behaviour change. Tests must be deterministic: block on events or barriers instead of
  asserting wall-clock budgets.
- Update the docs that describe what you changed: `specs/` for anything on the wire or on disk, the guides for
  anything a user does, and `CHANGELOG.md` for anything a user would notice.
- Everything in the repository is written in English: code, comments, specs and docs.
- Optional: reference the task ID from [`docs/MASTER-PLAN.md`](docs/MASTER-PLAN.md) (`PXX-TYY`) when a change belongs
  to a planned task.

### Changes with extra requirements

| You change… | You also need |
|---|---|
| The `.llog` format, the `latticelog.jsonl` exchange format or evidence documents | A spec update, a version bump and **new** golden files (never edit existing ones), plus a pass of the independent reader in `tools/pyllog` |
| A codec | Lab results against the baselines in `research/`, including the adversarial workloads |
| The ingest protocol or server API | A spec update in `specs/` and compatibility notes in `docs/compatibility.md` |
| The hot path or the storage engine | A before/after benchmark (`benchmarks/`) |
| Anything that parses input or handles I/O | A note on the security impact (see `docs/security/threat-model.md`) |
| Benchmark numbers in docs | The report in `reports/` they come from; never a number without its machine and method |

### Absolute blockers

A PR is not merged if it can cause any of these:
- silent corruption or deletion;
- a durable ACK before the data is persisted;
- access across tenants;
- decoding that depends on state that was never persisted;
- output that hides truncation;
- a security regression;
- a compatibility break without a migration path.

## Labels

| Label | Meaning |
|---|---|
| `good first issue` | Small, well scoped, verification steps included |
| `help wanted` | The maintainer would welcome an outside contribution |
| `bug`, `enhancement`, `documentation`, `question` | Kind of issue |

## Code of conduct

Participation is governed by the [Code of Conduct](CODE_OF_CONDUCT.md).
