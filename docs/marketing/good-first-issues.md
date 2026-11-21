# Proposed starter issues (not published)

Drafts for the maintainer to review and open by hand. Each one was checked against the code on 2026-11-21, is small,
and says how to verify it. Suggested labels in brackets.

---

### 1. Translate the golden-file READMEs to English  [good first issue, documentation]

`tests/GoldenFiles/v0.1/README.md`, `v0.2/README.md` and `v0.3/README.md` are written in Spanish, while CONTRIBUTING
says everything in the repository is in English.

- **Do:** translate the three files; keep every command and file name unchanged.
- **Don't:** touch the `.llog` or `.jsonl` files next to them (they are frozen test data).
- **Verify:** `python -I tools/pyllog/llog.py verify-golden tests/GoldenFiles/v0.1 tests/GoldenFiles/v0.2 tests/GoldenFiles/v0.3`
  still passes (it does not read the READMEs, but run it anyway).

### 2. A favicon for the web console  [good first issue, area: console]

The console (`src/LatticeLog.Server/ui/index.html`) has no icon, so browser tabs show a generic one. The logo is in
`docs/assets/logo.svg`. The console's CSP allows `img-src 'self' data:`, so an inline `data:` SVG works without a new
route.

- **Do:** add `<link rel="icon" href="data:image/svg+xml,...">` with the (URL-encoded) logo.
- **Verify:** `llog ui` (or `latticelog service run` with `LATTICELOG_CONFIG_DIR`/`LATTICELOG_DATA_DIR` set to scratch
  folders, then `latticelog ui`): the tab shows the icon and the browser console reports no CSP violation.

### 3. Say in `--help` that `--explain` and summaries go to stderr  [good first issue, area: cli]

`latticelog query … --explain > out.txt` writes the plan to the terminal, not to the file, which surprised the author of
`scripts/check-examples.sh`. The behaviour is right (results on stdout, diagnostics on stderr); the help text does not
say so.

- **Do:** add one line to the `query` help in `src/LatticeLog.Cli/Program.cs` and to `docs/guides/troubleshooting.md`
  if the wording changes.
- **Verify:** `dotnet run --project src/LatticeLog.Cli -- query --help`.

### 4. A Python example without structlog  [good first issue, area: sdk-python]

`examples/developer-flow` uses structlog. Many services use the standard `logging` module, which the SDK supports
through `latticelog.LatticeLogHandler`, but no example shows it.

- **Do:** `examples/python-logging/app.py` that writes to `logs/<service>` in local mode with `LatticeLogHandler`, logs
  an exception, and a README with the run command plus `latticelog errors` on the result.
- **Verify:** the README's commands work from a clone with only the standard library.

### 5. Prebuilt executables for macOS ARM64 and Linux ARM64  [help wanted, area: release]

`.github/workflows/release.yml` builds `win-x64` and `linux-x64` only, while CI already tests `macos-latest` (ARM64)
and `ubuntu-24.04-arm`. `llog update` would also need to pick the right asset name.

- **Do:** add matrix entries (`osx-arm64` on `macos-latest`, `linux-arm64` on `ubuntu-24.04-arm`) and teach
  `src/LatticeLog.Cli/UpdateCommands.cs` the new asset names.
- **Verify:** push a test tag on a fork; the release has four executables and `SHA256SUMS` covers them.
- **Note:** macOS executables downloaded from the web are quarantined by Gatekeeper; document `xattr -d
  com.apple.quarantine` or decide on signing (maintainer decision).

### 6. Run the hot-path and latency benchmarks on Linux  [help wanted, benchmarks]

All published numbers come from one Windows machine that is unstable under load (`docs/benchmarks.md`). A run on
another machine is the most useful outside contribution to the reports.

- **Do:** `dotnet run -c Release --project benchmarks/LatticeLog.Benchmarks -- --filter '*Enabled*'` and `-- latency`,
  then open an issue (or a PR adding `reports/…-linux.md`) with hardware, OS, .NET version and the raw output.
- **Verify:** the report states machine, method and any deviation from the original commands.

### 7. A `docs/guides` page for the server's HTTP API with curl examples  [good first issue, documentation]

`specs/server-api.md` is normative but dense. A how-to with `curl` calls for `/v1/search`, `/v1/events/{ref}` and
`/v1/stats`, starting from `docs/deploy/single-node.md`, would help people evaluate the server.

- **Verify:** every curl command was run against `dotnet run --project src/LatticeLog.ServerHost` with a test token.

### 8. Align the source-built version with the releases  [help wanted, maintainer decision]

`Directory.Build.props` says `0.1.0-alpha.1`; the latest tag is `v0.1.0-alpha.7`. Release builds pass the tag's version,
so only source builds and the MCP `serverInfo` (`src/LatticeLog.Mcp/McpServer.cs`) report the old one.

- **Do:** agree on a scheme first (for example, bump `Version` after each tag, or derive it from `git describe`),
  then make `McpServer` report the assembly's informational version instead of a literal.
- **Verify:** `latticelog --version` from a source build and `initialize` over MCP report the same version.
