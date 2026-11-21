# Install once: the per-user executable, service and MCP setup

> Moved here from the README. Quick path: run the one-line installer below, then `latticelog roots add <folder>`.

Like Redis or Ollama: install one file per user, connect your AI tools once, and from then on every service that
writes LatticeLog format into its folder can be asked about **by name**. No .NET, no administrator rights, nothing
system-wide, and nothing listens beyond `127.0.0.1`.

**One-line installer.** It downloads the newest release for your platform (the newest pre-release while there is no
stable one), refuses it unless its SHA-256 matches `SHA256SUMS`, and runs `latticelog setup`, which asks which MCP
clients to configure.

```powershell
irm https://raw.githubusercontent.com/lreyesm1999/LatticeLog/main/scripts/install.ps1 | iex
```

```bash
curl -fsSL https://raw.githubusercontent.com/lreyesm1999/LatticeLog/main/scripts/install.sh | sh
```

To pass options, run the script with arguments instead:

```powershell
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/lreyesm1999/LatticeLog/main/scripts/install.ps1))) -Clients claude-code -Yes
# also: -Version 0.1.0, -NoService, -NoPath, -DryRun (show what setup would do, change nothing)
```

```bash
curl -fsSL https://raw.githubusercontent.com/lreyesm1999/LatticeLog/main/scripts/install.sh | sh -s -- --clients claude-code --yes
curl -fsSL https://raw.githubusercontent.com/lreyesm1999/LatticeLog/main/scripts/install.sh | LATTICELOG_VERSION=0.1.0 sh
```

From a checkout, the same scripts use a local build when there is one (`scripts/install.ps1 -Build`,
`sh scripts/install.sh --build`) and download otherwise. The Windows script runs in Windows PowerShell 5.1 and
PowerShell 7; the shell script needs `curl` or `wget`. macOS and Linux ARM64 have no release build yet: use `--build`.

**Prebuilt executables.** Every [GitHub release](https://github.com/lreyesm1999/LatticeLog/releases) (pre-releases
for now) carries `latticelog-win-x64.exe`, `latticelog-linux-x64` and `SHA256SUMS`. They are single-file Native AOT
builds: no .NET runtime is needed to run them. Check the hash before running one:

```powershell
(Get-FileHash .\latticelog-win-x64.exe -Algorithm SHA256).Hash.ToLower()   # compare with the line in SHA256SUMS
.\latticelog-win-x64.exe setup   # installs itself as %LOCALAPPDATA%\Programs\LatticeLog\latticelog.exe
```

```bash
sha256sum -c SHA256SUMS --ignore-missing
chmod +x latticelog-linux-x64 && ./latticelog-linux-x64 setup   # installs itself as ~/.local/bin/latticelog
```

macOS and ARM64 have no prebuilt executable yet: build from source as below.

**Windows (PowerShell), from source.** Build the single-file executable once, then run `setup`. Building needs the
.NET 10 SDK and Visual Studio 2022 Build Tools with *Desktop development with C++* (the native linker). Running needs
neither.

```powershell
pwsh scripts/publish-aot.ps1                        # -> artifacts\aot\win-x64\latticelog.exe (Native AOT, one file)
.\artifacts\aot\win-x64\latticelog.exe setup        # asks which MCP clients to configure
# without questions: latticelog.exe setup --clients claude-desktop,claude-code --yes
```

**Linux / macOS.** Run `sh scripts/install.sh --build`, which needs the .NET 10 SDK plus clang (or the Xcode command
line tools), or `sh scripts/install.sh ./latticelog` with a prebuilt binary.

`setup` does three things and prints each one:

1. **Copies the executable** to `%LOCALAPPDATA%\Programs\LatticeLog` (Linux/macOS: `~/.local/bin`) and adds that
   folder to your user `PATH`.
2. **Registers and starts the background service** for your user. On Windows it is a Task Scheduler task at logon (or
   `--method run-key`); on Linux a systemd user unit; on macOS a launchd agent. The service owns the source catalog:
   - indexes Python sources incrementally;
   - follows migrations made with `follow=true`;
   - compacts and applies retention to LatticeLog's own copies, never to your original files;
   - serves every MCP client from one process, so several editors at once never contend for an index.
3. **Adds a `latticelog` MCP server entry** to the clients you pick: Claude Desktop, Claude Code, Cursor, VS Code.
   Existing config files are backed up first, and an existing entry is replaced only after you confirm.

Then register the folder your services write into, and ask:

```bash
latticelog roots add C:/dev/my-services/logs   # once; services under it are found by name
latticelog service status                      # sources, last indexed time, disk use, errors
llog ui                                        # web console in your browser (short for latticelog service ui --open)
```

`latticelog mcp`, the command the clients run, is a thin shim. It forwards to the service and starts it when it isn't
running. If the service cannot start, the shim serves in-process as before; `--no-service` forces that. If the service
restarts mid-session, the shim reconnects and resends the requests that were in flight.

| | |
|---|---|
| service control | `latticelog service status`, `start`, `stop`, `restart`, `install`, `uninstall`, `ui` |
| retention | `latticelog sources retention '*' --max-age 30d` (default for all), `latticelog sources retention legacy-app --max-mb 500` |
| service files | endpoint and log in `<data>/service/` (`%LOCALAPPDATA%\LatticeLog\service`, `~/.local/share/latticelog/service`) |

**Update.** `llog update` installs the newest release from GitHub Releases:

```bash
llog update --check     # what is installed, what is available; changes nothing
llog update             # asks, then installs (-y to skip the question)
```

1. It downloads the executable for your platform (Windows x64, Linux x64) and `SHA256SUMS`, and refuses the file if
   the hash does not match.
2. It runs the downloaded executable's own `setup`, which stops the service, swaps the executable (a running one, such
   as an editor's MCP server, is moved aside), and registers and restarts the service.

Data, config and the MCP entries in your editors are kept. Restart your AI tools to load the new MCP server. Use
`--version 0.2.0` for a specific release, or `--prerelease` to include pre-releases.

Releases are published by [`release.yml`](../../.github/workflows/release.yml) when a version tag is pushed: run
`git tag v0.2.0` and then `git push origin v0.2.0`. A tag with a hyphen (`v0.2.0-beta.1`) becomes a pre-release.

**Uninstall.** Run `latticelog setup --uninstall`. It:

- stops and unregisters the service;
- removes the `latticelog` entry from the four clients' user configs, keeping a backup;
- takes the folder off your `PATH`;
- deletes the executable.

Indexes and migrated sources stay in the data folder unless you add `--remove-data`. Your original log files are never
touched. To remove only the service, run `latticelog service uninstall`.

Design and protocol: [`specs/local-sources.md` §6](../../specs/local-sources.md#6-background-service).
