# MCP: give Claude Code, Cursor, Claude Desktop or VS Code your logs

`latticelog mcp` is a [Model Context Protocol](https://modelcontextprotocol.io) server over stdio. It lets a coding
agent list your log sources **by service name** and query them with the same tools as the CLI. It is read-only, with
one exception you must ask for (`migrate_logs`, which writes a *new* source and never modifies the original files).
LatticeLog runs no model; the agent does the reasoning, LatticeLog supplies the records and says what it covered.

## 1. Have the `latticelog` command

Either install the single executable (recommended, [Install](install.md)):

```powershell
.\latticelog-win-x64.exe setup --clients claude-code,cursor --yes   # also starts the per-user service
```

or build from source and let `mcp install` point the client at `dotnet <path>/latticelog.dll`:

```bash
dotnet build LatticeLog.sln -c Release
dotnet run -c Release --no-build --project src/LatticeLog.Cli -- mcp install --client cursor --dry-run
```

## 2. Tell LatticeLog where your services write

```bash
latticelog roots add ./logs          # per-user; every service folder or .NET store under it is a source
latticelog sources                   # what the agent will see
```

Or commit a `latticelog.toml` to your project (`roots = ["logs"]`); see
[`specs/local-sources.md`](../../specs/local-sources.md#project-file-latticelogtoml-optional).

## 3. Register the server with your client

`--dry-run` prints the file and the entry without writing anything. Existing files are backed up, and an existing
`latticelog` entry is replaced only after you confirm (or with `--yes`).

| Client | Command | File written (default scope) |
|---|---|---|
| Claude Code | `latticelog mcp install --client claude-code` | `.mcp.json` in the current folder (project). `--scope user` writes `~/.claude.json` |
| Cursor | `latticelog mcp install --client cursor` | `~/.cursor/mcp.json` (user). `--scope project` writes `.cursor/mcp.json` |
| Claude Desktop | `latticelog mcp install --client claude-desktop` | the app's `claude_desktop_config.json` (user) |
| VS Code | `latticelog mcp install --client vscode` | `.vscode/mcp.json` (project) |

The entry it writes looks like this (from a `--dry-run` with a source build; an installed executable is just
`"command": "<path>/latticelog", "args": ["mcp"]`):

```json
{
  "mcpServers": {
    "latticelog": {
      "command": "C:\\Users\\you\\AppData\\Local\\Microsoft\\dotnet\\dotnet.exe",
      "args": ["C:\\src\\LatticeLog\\src\\LatticeLog.Cli\\bin\\Release\\net10.0\\latticelog.dll", "mcp"],
      "env": { "DOTNET_ROOT": "C:\\Users\\you\\AppData\\Local/Microsoft/dotnet" }
    }
  }
}
```

VS Code files use a top-level `"servers"` key instead of `"mcpServers"`; `mcp install` writes the right one. Restart the client (or reload its MCP servers). To remove the entry: `latticelog mcp uninstall --client <name>`.

## 4. The tools

| Tool | What it returns |
|---|---|
| `list_sources` | Sources by name with type, record counts and time range |
| `search_events` | Events matching a typed filter, merged across sources, with refs, coverage and a cursor |
| `get_event` | One event by `llog://` reference, with provenance |
| `get_trace` | A trace's timeline across sources, with operation relations and coverage |
| `get_related_events` | Events explicitly related (same trace, operation, parent/child, correlation id); never "nearby in time" |
| `get_error_groups` | Errors grouped by deterministic fingerprint, with counts and sample refs |
| `get_context_pack` | Context Pack V2: progressive summary/investigation/evidence with a full-payload budget, stable refs and continuation |
| `get_event_schema` | Declared event contracts: ids, names, field types, sensitivity |
| `get_overview` | Counts by severity, event and service, plus top error groups |
| `preview_migration` | What converting a text/JSON log file would keep (read-only) |
| `migrate_logs` | Converts a log file into a new source (writes; only on explicit request) |

Every tool takes `source` (a name, a list, or `'*'`). Results carry a `coverage` object (`truncated`,
`partial_results`, `sampling`…) so an agent can tell "not found" from "not looked at", and they list which fields hold
text copied from logs, so the client treats them as data and not as instructions.

Context Pack V2 accepts `budget_bytes`, estimated `budget_tokens`, `max_events`, `max_groups`,
`detail_level` (`summary`, `investigation`, or `evidence`), `include_evidence`, and `continuation_token`. For example,
start with a compact view and expand only if needed:

```text
get_context_pack(source="orders-api", since="1h", detail_level="summary",
                 budget_tokens=1500, max_groups=10, include_evidence=false)
```

The response labels the token estimate and returns stable `llog://` refs. Pass one to `get_event` for the complete
event under the active redaction policy; this does not repeat the search. If `coverage.continuation_token` is present,
pass it back unchanged to inspect the next candidate window.

## 5. Ask

In your editor's agent chat, for example:

> Which errors did `orders-api` log in the last hour? Group them and show me one full event per group.

A typical agent calls `list_sources`, then `get_error_groups(source='orders-api', since='1h')`, then `get_event` for
each group's sample ref. A scripted version of that conversation, with real output, is in
[Investigating an incident with an agent](investigate-with-an-agent.md).

## How it runs

`latticelog mcp` is a thin shim. When the per-user service is installed it forwards to it (one index shared by every
editor; the service is started if it is not running) and falls back to serving in-process if the service cannot
start. `--no-service` forces in-process mode; `latticelog mcp <dir>` binds the server to a single store. Design:
[`specs/local-sources.md` §6](../../specs/local-sources.md#6-background-service).

Problems: [Troubleshooting](troubleshooting.md#mcp).
