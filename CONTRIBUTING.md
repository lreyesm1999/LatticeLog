# Contributing to LatticeLog

Thanks for your interest. LatticeLog is a free, MIT-licensed project. Issues, discussions and pull requests are all welcome.

## Before you start

- **Bugs**: open an issue with the version, OS, a minimal reproduction and what you expected. If the bug touches data integrity (corruption, lost or duplicated records, a false durability ACK), say so in the title. Those are treated as blockers.
- **Larger changes**: open an issue first so the design can be discussed before code is written.
- **Security issues**: never in a public issue. See [`SECURITY.md`](SECURITY.md).

## Development setup

```bash
dotnet build LatticeLog.sln -c Release
dotnet test  LatticeLog.sln -c Release
python -I tools/pyllog/llog.py verify-golden tests/GoldenFiles/v0.1 tests/GoldenFiles/v0.2
dotnet format whitespace LatticeLog.sln --verify-no-changes
```

The .NET SDK version is pinned in [`global.json`](global.json). The Python and PHP SDK tests run when `python` and `php` are on the `PATH`.

## Pull requests

- Keep each PR to one concern, branched from `main`. CI must be green on Linux, macOS and Windows.
- Add or update tests with every behaviour change. Tests must be deterministic: block on events or barriers instead of asserting wall-clock budgets.
- Update the docs that describe what you changed: `specs/` for anything on the wire or on disk, and `CHANGELOG.md` for anything a user would notice.
- Code and code comments are in English. Product and operations docs under `docs/` are currently in Spanish. English contributions there are welcome too.

### Changes with extra requirements

| You change… | You also need |
|---|---|
| The `.llog` format, the `latticelog.jsonl` exchange format or evidence documents | A spec update, a version bump and **new** golden files (never edit existing ones), plus a pass of the independent reader in `tools/pyllog` |
| A codec | Lab results against the baselines in `research/`, including the adversarial workloads |
| The ingest protocol or server API | A spec update in `specs/` and compatibility notes in `docs/compatibility.md` |
| The hot path or the storage engine | A before/after benchmark (`benchmarks/`) |
| Anything that parses input or handles I/O | A note on the security impact (see `docs/security/threat-model.md`) |

### Absolute blockers

A PR is not merged if it can cause any of these:
- silent corruption or deletion;
- a durable ACK before the data is persisted;
- access across tenants;
- decoding that depends on state that was never persisted;
- output that hides truncation;
- a security regression;
- a compatibility break without a migration path.

## Code of conduct

Participation is governed by the [Code of Conduct](CODE_OF_CONDUCT.md).
