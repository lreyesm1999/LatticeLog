# Troubleshooting

## Building

**`A compatible .NET SDK was not found` / `framework 10.0.0 not found`.** The SDK version is pinned in
[`global.json`](../../global.json). Install that SDK. If you installed it per user (not system-wide), set `DOTNET_ROOT`
to its folder and put it first on `PATH` before running `latticelog.dll` or the benchmarks, otherwise the app host
finds the system `dotnet`, which may not have .NET 10.

**Native AOT publish fails with a linker error.** Native AOT needs a native toolchain: on Windows, Visual Studio 2022
Build Tools with *Desktop development with C++*; on Linux, `clang`; on macOS, the Xcode command line tools. You only
need it to build the single executable; `dotnet build` and `dotnet run` do not.

**Random compiler crashes or test failures under heavy parallel load.** Rule out the machine first: rerun with
`dotnet build -m:1` and, for tests, fewer CPUs. The reference development machine is known to corrupt memory under
all-core load (see [`docs/benchmarks.md`](../benchmarks.md#hardware)).

## Running the examples

**The ASP.NET Core example's store is not where I asked.** `dotnet run` starts web projects in their project folder;
pass an absolute path (`"$PWD/aspnet-store"`).

**`demo_api.py`: `ModuleNotFoundError: No module named 'latticelog'`.** The script adds `../../sdks/python` to the
path, so run it from `examples/developer-flow` inside the repository, or `pip install -e sdks/python`. It also needs
`pip install structlog`.

## CLI

**`'<dir>' is not a LatticeLog store (missing STORE.json)`.** The path is not a store folder. Use the folder you passed
to `LatticeLogger.CreateLocal`, or use a source name with the commands that take one (`latticelog sources`).

**Exit codes.** `0` ok, `1` usage, `2` not found, `3` integrity issues (from `verify`), `4` partial results, `5` data
error, `6` store locked, `10` internal.

**`--explain` output is missing when I redirect.** Explanations and summaries go to stderr, results to stdout:
redirect both (`> out.txt 2>&1`).

**A query returns fewer results than expected.** Check the footer line and `coverage`: `more results: --cursor …`
means there are more pages; `partial_results` means a segment could not be read, and `latticelog verify` tells you
which and why.

## MCP

**The client does not show LatticeLog's tools.** Restart the client after `mcp install`. Then check the entry with
`latticelog mcp install --client <name> --dry-run`. Claude Code and VS Code default to the **project** scope (a file in
the current folder), so run the command in the folder you open in the editor, or pass `--scope user` for Claude Code.

**`list_sources` is empty.** Register the folder your services write into: `latticelog roots add <folder>`, or add a
`latticelog.toml` with `roots = [...]` to the project. `latticelog sources` shows what the server sees.

**Config changes made from an app do not reach the service (Windows).** Fixed in recent releases: config commands now
send the change to the running service. Update with `llog update`.

**Several editors at once.** That is what the background service is for: one index shared by every client. Check it
with `latticelog service status`.

## Service and console

**`llog ui` does not open.** Run `latticelog service status`. The console URL carries the service token in the
`#fragment`; it reads every source of your user, so do not share the link.

**The service will not start.** Its log is in `<data>/service/` (`%LOCALAPPDATA%\LatticeLog\service` on Windows,
`~/.local/share/latticelog/service` on Linux). `latticelog service run --verbose` runs it in the foreground.

Still stuck? See [`SUPPORT.md`](../../SUPPORT.md).
