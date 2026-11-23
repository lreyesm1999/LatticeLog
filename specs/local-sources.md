# Local sources: logs found by service name, and migration of traditional logs

Status: implemented (alpha). Code: `src/LatticeLog.Sources`, `src/LatticeLog.Mcp`, `src/LatticeLog.Cli/SourceCommands.cs`,
`sdks/python/latticelog/_local.py`, `sdks/python/latticelog/_structlog.py`, `sdks/rust/src/folder.rs`, `sdks/rust/src/writer.rs`, `sdks/typescript/src/folder.ts`, `sdks/typescript/src/logger.ts`.
Tests: `tests/LatticeLog.Tests/SourcesTests.cs`, `sdks/python/tests/test_local.py`, `sdks/rust/tests/local.rs`,
`sdks/typescript/test/local.test.mjs`.

The Rust SDK (`latticelog::open`) and the TypeScript SDK (`open()` of the npm package `latticelog`) write the same
layout and the same documents (section 1, *Folder layout*). In server mode they also keep `cursor.json`
(`{"committed_through": N}`) in each process folder they ship. Node.js has no portable file lock. The TypeScript
SDK claims `p-N` by creating `LOCK` exclusively with its pid, host and start time. It takes over a `LOCK` only when
that process is certainly gone (`sdks/typescript/README.md`, *Process folders without an OS lock*).

The developer flow has three parts:

1. Services write their logs **directly in LatticeLog's own format**, each into its own folder.
2. LatticeLog (CLI and MCP server) **finds the logs by service name**. Nobody passes paths or formats.
3. On explicit request, **traditional log files are migrated** into the same format, so old and new logs are queried
   the same way. The originals are never modified, moved or deleted.

A background daemon and an installer are out of scope here; the CLI and the MCP server do the indexing when they are used.

## 1. Python local mode (no server)

```python
log = latticelog.open("logs/orders-api", service="orders-api", environment="dev",
                      durability="balanced", max_age=7 * 86400, max_bytes=512 * 2**20)
```

### Folder layout

```text
logs/orders-api/
  latticelog.source.json                 manifest (below)
  p-1/                                   one folder per live process
    LOCK                                 held (OS lock) by the process writing p-1
    process.json                         {"producer_id": "<32 hex>"}
    latticelog-00000000000000000001.jsonl
    latticelog-00000000000000004213.jsonl   named after the first sequence it holds
  p-2/ ...
```

Manifest `latticelog.source.json` (the Rust SDK adds `"producer":"latticelog-rust"` and the TypeScript SDK
`"producer":"latticelog-typescript"`, and `list_sources` then reports the kind `rust-local` or `typescript-local`
instead of `python-local`; everything else is identical):

```json
{"format": "latticelog.source", "version": 1, "service": "orders-api", "environment": "dev",
 "data_format": "latticelog.jsonl", "data_version": 1, "producer_id": "<32 hex>",
 "layout": "process-folders", "created": "2026-11-19T10:00:00Z"}
```

- The manifest is created once with `O_EXCL` (two processes starting together agree on one). A folder belongs to one
  service: opening it with another `service` raises an error.
- Each data file is a complete, self-contained `latticelog.jsonl` v1 document ([jsonl-exchange.md](jsonl-exchange.md)):
  a header line (`store_id` = the process folder's `producer_id`), `context` lines before first use, then `record` lines.
  Every record carries `seq`, a **dense sequence per process folder** that continues across restarts.
- **One subfolder per process.** A process takes the first `p-N` folder whose `LOCK` it can lock (`msvcrt.locking` /
  `flock`, released by the OS when the process dies) and continues that folder's sequence; if all are busy it creates
  `p-<max+1>`. The number of folders therefore stays at the maximum number of concurrent processes. A forked child
  (`os.register_at_fork`) takes its own folder. LatticeLog shows all process folders as **one source**.
- **Recovery.** At start the newest file's torn last line (crash mid-write) is cut off; the next sequence follows the
  last complete record. A new file is started at every start and when a file reaches `file_max_bytes` (16 MiB).
- **Durability** (same names as the server spool): `fast` (OS buffers, flushed at flush/close/rotation), `balanced`
  (every record handed to the OS), `durable` (fsync per record). A rotated file is always fsynced before the next one
  is opened.
- **Retention** by age (`max_age`, seconds or `timedelta`) and/or size (`max_bytes`, whole source folder): whole
  **closed** files are deleted, oldest first, at start and at each rotation. The newest file of each process folder
  may still be written and is never deleted, and a closed file was fsynced when it was closed, so nothing unconfirmed
  is ever deleted. (What the LatticeLog index already holds stays queryable after the files are gone.)

### structlog

```python
structlog.configure(processors=[
    structlog.contextvars.merge_contextvars,
    structlog.processors.add_log_level,
    structlog.processors.TimeStamper(fmt="iso"),
    latticelog.structlog_processor(log),          # the one added line
    structlog.dev.ConsoleRenderer(),
])
```

The processor writes the event and passes the event dict on unchanged (`drop=True` makes LatticeLog the only output).
Mapping — the same rules the .NET importer applies to structlog JSON, so live and migrated logs answer the same queries:

| event dict | record |
|---|---|
| `event` (identifier-like: letters, digits, `_ . : - /`, ≤ 128 chars) | event name |
| `event` (free text, e.g. `"Heartbeat failed for group x"`) | body (message) |
| `level` (else the method name; `exception` → error, `critical` → fatal) | severity |
| `timestamp` (ISO 8601 or epoch; naive = local time) | time (else the time of the call) |
| `exc_info` (`True`, tuple or exception) | exception with the **real type** `module.QualName` (builtins unprefixed), message, traceback and `__cause__`/`__context__` chain |
| `exception` string (`format_exc_info`) / list (`dict_tracebacks`) | exception, type parsed from the traceback |
| `trace_id` (32 hex) / `span_id` (16 hex) | trace fields |
| every other key | typed attribute (`int` stays Int64, `float` Double, `datetime`, `UUID`, `Decimal`, dicts, lists …) |

Write failures never raise into the application (counted in `processor.errors`).

`LatticeLogHandler` (stdlib `logging`) recognizes structlog's `ProcessorFormatter.wrap_for_formatter`, where
`record.msg` is the event dict: it applies the same mapping instead of `record.getMessage()` (which flattened the dict
to text and lost the event name and types). Plain `logging` records keep their formatted message.

## 2. Finding sources by name

### Registration

- **Per-user roots**: `latticelog roots add <folder>` (also `remove`, `list`).
- **Migration allowlist**: `latticelog allow add <folder>`.
- Stored in `config.json` under `%APPDATA%\LatticeLog` (Windows) or `~/.config/latticelog`:
  `{"format":"latticelog.config","version":1,"roots":[...],"allow":[...],"data_dir":null}`.
- **Data folder** (indexes, migrated sources): `%LOCALAPPDATA%\LatticeLog` or `~/.local/share/latticelog`, or
  `data_dir` in the config. `LATTICELOG_CONFIG_DIR` and `LATTICELOG_DATA_DIR` override both.

### Project file `latticelog.toml` (optional)

Searched in the working directory and its parents (`--project <dir>` sets the start):

```toml
roots = ["logs"]                       # project-relative roots

[sources]                              # explicit names (win over discovered names)
demo-api = "logs/demo-api"
billing = { path = "../billing/logs", environment = "prod" }

[migrate]
allow = ["old-logs"]                   # adds to the migration allowlist
```

Only this TOML subset is read: comments, `[table]`/`[a.b]` headers, bare or quoted keys, basic and literal strings,
integers, booleans, one-line arrays and inline tables. Anything else is an error with file and line.

### Discovery

Each root is walked breadth-first (depth ≤ 8, ≤ 50,000 folders; hidden folders, `node_modules`, `__pycache__`,
`site-packages`, `venv`/`env`, symbolic links and the data folder are skipped). A folder is a source when it holds

| marker | kind | name |
|---|---|---|
| `latticelog.source.json` | `python-local` (`rust-local` when the manifest's `producer` is `latticelog-rust`, `typescript-local` when it is `latticelog-typescript`) | the manifest's `service` |
| `STORE.json` (`"format":"latticelog.store"`) | `dotnet-store` | the manifest's `service` if a `latticelog.source.json` sits next to it, else the folder name |

and its subfolders are not searched further. `[sources]` entries name a folder directly. Migrated sources come from
the data folder. Name collisions keep every source: the later one is listed as `name@environment`, else `name#2`.

### Indexing Python sources

Each Python source gets an incremental `.llog` store in `<data>/index/<service>-<hash of path>/store` with a checkpoint
`checkpoint.json` (section 4). The CLI (`latticelog sources`) and the MCP server (before tool calls, at most every 2 s)
bring it up to date: only complete new lines are read (a line being written waits for its `\n`), records whose
producer `seq` was already indexed from that file are skipped, and a crash or a concurrent indexer never produces a
duplicate. An idle source costs a size comparison per file. A writer lock on the index makes a second MCP server read
what is indexed instead of indexing in parallel. When an index has more than 32 segments it is compacted (`balanced`).

.NET stores are read directly, also while their writer runs (the reader sees sealed segments and the complete blocks
of the active one).

## 3. MCP tools

`latticelog mcp` (no store argument) serves every source. `latticelog mcp <dir>` keeps the previous behaviour: one store,
strictly read-only, no migration tools.

| tool | annotations | notes |
|---|---|---|
| `list_sources` | read-only | name, kind, path, service, environment, processes, records, bytes, time range, roots and allowlist |
| `search_events`, `get_event`, `get_trace`, `get_related_events`, `get_error_groups`, `get_context_pack`, `get_event_schema`, `get_overview` | read-only | new optional `source`: a name, a list of names, or `"*"` (default: all) |
| `preview_migration(path)` | read-only | section 5 |
| `migrate_logs(path, source_name, follow=false)` | `readOnlyHint: false`, `destructiveHint: false`, `idempotentHint: true` | the only write tool; section 5 |

Merging across sources: `search_events` merges the per-source results by event time (k-way) and returns a composite
cursor holding one position per source, so paging returns every event exactly once. Evidence documents are merged
field by field: coverage numbers summed and flags OR-ed, timelines merged by time (`dt_us` recomputed), error groups
ranked by count, count tables added. Every event, timeline entry, group and schema entry carries `source`. `get_event`
and `get_related_events` find the source from the store id inside the `llog://` reference (a bare sequence number
needs `source`); related events are searched in the anchor's source. Log text stays marked untrusted
(`trust.untrusted_fields`, `_meta.untrusted_content`).

### Client setup

`latticelog mcp install --client claude-desktop|claude-code|cursor|vscode [--scope user|project] [--yes] [--dry-run]`
merges an entry into the client's configuration, keeping every other key:

| client | file | key |
|---|---|---|
| claude-desktop | `%APPDATA%\Claude\claude_desktop_config.json` (macOS `~/Library/Application Support/Claude/…`, Linux `~/.config/Claude/…`) | `mcpServers` |
| claude-code | project `.mcp.json` (default) or user `~/.claude.json` | `mcpServers` |
| cursor | user `~/.cursor/mcp.json` (default) or project `.cursor/mcp.json` | `mcpServers` |
| vscode | project `.vscode/mcp.json` (default) or the user `Code/User/mcp.json` | `servers` (with `"type":"stdio"`) |

The command is this build of `latticelog` (or `dotnet latticelog.dll`); `DOTNET_ROOT` (framework-dependent builds only), `LATTICELOG_CONFIG_DIR` and
`LATTICELOG_DATA_DIR` are passed in `env` when set. Clients that do not start servers in the project folder get
`--project <dir>` when a `latticelog.toml` exists. An existing entry with the same name, or a file with comments
(which a rewrite cannot keep), is changed only after the user answers `y` (or with `--yes`); without a terminal the
command refuses. The previous file is saved as `<file>.latticelog-backup-<UTC time>`.

## 4. Checkpoints (incremental import, shared by indexing and migration)

`checkpoint.json` (`"format":"latticelog.checkpoint","version":1`) holds the store's last sequence, the range being
imported (`pending`) and, per file: `path`, `format`, `head_len`/`head_sha256` (first ≤ 1 KiB), `offset` (bytes
consumed, always at a line end), `tail_len`/`tail_sha256` (≤ 1 KiB before `offset`), `skip` (records of the range at
`offset` already stored), `records`, `unparsed`, `last_producer_seq` and, for `latticelog.jsonl`, the definition lines
(header, contexts, schemas) seen before `offset`.

- **Which file is which.** A file continues a checkpoint entry when its first bytes and the bytes just before the
  entry's offset are unchanged and it is at least that long — first under the same path, else under another path
  (renamed by rotation: `app.log` → `app.log.1`; the remainder of the renamed file is imported, nothing twice). A file
  that is shorter, or whose head/tail changed, is a new generation (truncated, copy-truncate rotation, replaced) and is
  read from the start as a new entry.
- **Only complete lines.** The range ends after the last `\n`. For migrations that do not follow, an unterminated last
  line of a file untouched for 5 minutes is taken as finished and imported.
- **Never twice.** Before a range is written the checkpoint records `pending` and the store's last sequence. After a
  crash, `StoreWriter` recovery yields the durable last sequence; the difference is exactly the number of records of
  the pending range already stored, which the next run skips (records are produced deterministically from the same
  bytes). Cancellation is honoured between batches (16,384 records) and saves the checkpoint with `skip`.

## 5. Migration of traditional logs

### Preview (read-only)

`preview_migration(path)` / `latticelog migrate <path> --preview` returns: the detected format and the importer that
will be used, the reason for the detection, bytes and line count, **5 sample records exactly as they would be stored**,
the field mapping (per JSON key, from the importer's own rules), an estimated store size (the first ≤ 20,000 lines /
8 MB are imported into a temporary store in the data folder, compacted with the `compact` policy, scaled to the file
size, then deleted), the unparsed lines in the sample, and what the conversion cannot keep (the `LossReport` of the
sample plus the known limits of the format). Nothing is written next to the file.

Format detection (first 64 lines / 256 KiB):

| detected | rule | importer |
|---|---|---|
| `latticelog.jsonl` | first line is a `latticelog.jsonl` header | `JsonlExchangeReader` (lossless) |
| OTLP-JSON, one request per line | first line is a JSON object with `resourceLogs` | `OtlpJson` per line |
| OTLP-JSON, one document | first line does not parse alone and `resourceLogs` appears | `OtlpJson` (imported once) |
| Serilog CLEF | ≥ half of the JSON lines have `@t`/`@mt`/`@m` | `StructuredJsonlImporter` |
| pino | numeric `level` with `time`/`pid`/`hostname` | `StructuredJsonlImporter` |
| structlog | `event` with `level`/`log_level`/`timestamp` | `StructuredJsonlImporter` (structlog events, Python tracebacks) |
| generic JSON lines | ≥ half of the sampled lines are JSON objects | `StructuredJsonlImporter` |
| plain text | otherwise | `RawTextImporter` (byte-exact body) |

### Migrate

`migrate_logs(path or glob, source_name, follow=false)` / `latticelog migrate <path|glob> --name <source> [--follow]`:

- **Safety.** `path` (or, for a glob, its folder and then every matched file, with links resolved) must be inside a
  registered root, the user allowlist or the project's `[migrate] allow`; the data folder itself is refused. Anything
  else is refused with a message that names the allowed folders and how to add one. Globs (`*`, `?`) are allowed in
  the file name only; compressed files are refused.
- **Originals.** Opened read-only with full sharing (`FileShare.ReadWrite | Delete`), never modified, moved, renamed,
  truncated or deleted. Nothing is written next to them.
- **Output.** Only into `<data>/migrated/<source_name>/`: `store/` (a LatticeLog store), `checkpoint.json` and
  `migration.json` (`"format":"latticelog.migration"`: name, patterns, follow, history of runs). The source is
  registered under `source_name` (`[A-Za-z0-9][A-Za-z0-9._-]{0,63}`, not the name of a live source).
- **Atomic registration.** A first migration is built in `<data>/migrated/.staging-<name>-<guid>/` and becomes the
  source by one directory rename after the import and the compaction. Cancellation or failure deletes the staging
  folder, so no half-registered source exists. Stale staging folders (> 6 h) are removed by later migrations.
- **Idempotent.** Re-running imports only new complete lines (section 4), detects rotation and truncation and never
  duplicates. Files matched later by the same glob are added.
- **Lines that cannot be parsed** are kept as text records (flag `LossyImport`, attribute `latticelog.import.error`),
  never dropped, and counted.
- **Compaction.** At the end, with the `compact` policy (as `latticelog compact --policy compact`).
- **Report.** Records imported (this run and total), unparsed lines kept as text, loss report, original bytes, final
  store bytes, per-file status, compaction and warnings (e.g. a file renamed by rotation).
- **Progress.** The MCP tool sends `notifications/progress` (bytes of new data, records) when the request carries a
  `progressToken`; the CLI prints it on stderr. `notifications/cancelled` (MCP) or Ctrl+C (CLI) stops between batches.
- **follow=true** keeps the source updated: every refresh (CLI `sources`, MCP tool calls) imports new complete lines of
  the registered patterns, without compaction. With the background service (section 6) this happens every few
  seconds, whether or not anyone is querying.

Measured on a copy of a real 377,618,155-byte structlog file (1,762,702 lines): detected as structlog JSON, 1,762,702
records imported, 0 unparsed, lossless mapping, 7,918,397 bytes after compaction, 7.1 s in total; a re-run imported 0
records in 44 ms; the copy's SHA-256 was unchanged.

## 6. Background service

One process per user (and data folder) does the work that every MCP client used to do for itself. It runs as
`latticelog service run`, is normally registered to start at logon, and is started on demand by the MCP shim.

### What it owns

- **Catalogs.** It opens the per-user catalog (roots from `config.json`) at start, and one catalog per
  `latticelog.toml` that a client asks for. A project catalog is dropped after a day unused. A catalog is reopened when
  `config.json` or its project file changes.
- **All writes to LatticeLog's own stores.** These share one gate: refreshes (discovery, incremental indexing of Python
  sources, followed migrations), maintenance and `migrate_logs`. The service therefore never contends with itself for
  an index's writer lock, however many editors are connected.
- **Background refresh**, every 5 s. Tool calls still refresh first when the last refresh is older than 2 s, as in
  in-process mode, and wait at most 5 s for a refresh that is already running.
- **Maintenance**, every 10 min. Indexes and migrated stores with more than 8 segments are compacted (`balanced`), and
  retention is applied.
- **Retention per source**, configured in `config.json` and set with `latticelog sources retention`:
  `"retention": {"*": {"max_age": "30d"}, "legacy-app": {"max_mb": 500}}`. It applies only to the stores LatticeLog owns
  (Python indexes and migrated sources), and it always keeps the newest segment so the checkpoint stays consistent.
  Original log files and .NET stores (written by their applications) are never touched.

### Transport

The service listens on `127.0.0.1` only: port 7462 by default, or any free port when that one is taken. When it
listens, it writes `<data>/service/service.json` with its pid, port and a random 256-bit token. On Unix the file has
mode 0600; on Windows it inherits the user-only ACL of `%LOCALAPPDATA%`. The file is removed when the service stops. A
lock file (`service.lock`, held exclusively) allows one service per data folder.

One port carries two protocols, told apart by the first byte:

- **Line protocol** (first byte `{`).
  1. The client sends one JSON line:
     `{"latticelog":"service/1","token":"…","op":"mcp|status|stop|ping|refresh|maintenance|config","project_dir":"…","config_dir":"…"}`.
  2. The service answers one line: `{"ok":true,…}` or `{"ok":false,"error":"…","message":"…"}`. It refuses a wrong
     token, or a config folder that differs from its own.
  3. After `mcp`, the connection carries MCP JSON-RPC lines both ways. They are served by the same `McpServer` as
     in-process mode, so tools, limits and results are the same. `list_sources` reports `"served_by":"service"` (or
     `"in-process"`).
  4. `config` carries one change to `config.json` in `"edit"`. The kinds are:
     - `{"kind":"folder","list":"roots|allow","action":"add|remove","folder":"<absolute>"}`;
     - `{"kind":"retention","name":"…","max_age":"30d","max_mb":N,"clear":false}`;
     - `{"kind":"get"}`.

     The service applies it to the file it reads and answers `{"ok":true,"changed":…,"config":{path, roots, allow,
     retention}}`. A malformed edit gets `bad_request`.

### Config changes go through the service

`latticelog roots|allow add|remove|list` and `latticelog sources retention` send their change to the running service,
which writes its own `config.json`. They write the file themselves only when no service answers, or when the service
predates the `config` operation. Its catalogs reopen when the file changes.

The reason is Windows packaged apps. A command started from inside an MSIX or Store app, such as Claude Desktop or a
terminal it opens, gets its writes to `%APPDATA%` redirected to the package's private copy
(`%LOCALAPPDATA%\Packages\<package>\LocalCache\Roaming\…`). The service is started by the Task Scheduler outside the
package, so it would never see such a change. When a command does write the file itself, it checks afterwards whether
the write landed in some package's `LocalCache` copy. It checks by effect, because processes such an app starts can be
redirected while `GetCurrentPackageFullName` reports no package. If the write was redirected, the command prints a
warning with the exact redirected path and how to apply the change where the service reads it.
- **HTTP** (`GET`/`HEAD`).
  - `/ui` serves the web console.
  - The read API the console uses treats each source as a namespace: `/v1/namespaces`, `/v1/search`,
    `/v1/traces/{id}`, `/v1/stats`, `/v1/catalog`. `/v1/status` returns the status document.
  - `/v1/sources` feeds the console's source explorer: every source as `list_sources` describes it (also the ones
    with nothing indexed yet), plus `last_indexed`/`last_error`/`retention` from the service, the registered `roots`
    (also the empty ones) and the data folder. `?refresh=1` rescans the roots first. The console builds its folder
    tree from each source's `origin` (`root:`/`toml-root:` folder, `toml:` project file, `migrated` grouped by the
    original file's folder) and falls back to `/v1/namespaces` (a flat list) against a server or an older service.
  - Every `/v1/*` route except `/v1/health` needs `Authorization: Bearer <token>`.
  - The `Host` header must be `127.0.0.1`, `localhost` or `[::1]` with the service's port, which blocks DNS rebinding.
    No CORS headers are sent.
  - `latticelog service ui` prints `/ui#token=…`. The console moves the token from the fragment, which is never sent
    to the server, into `sessionStorage`.

### The MCP shim

`latticelog mcp` reads the endpoint file and connects. When no service answers, it starts one and waits up to 15 s:

- **Registered:** through the registration (`schtasks /Run`, `systemctl --user start`, `launchctl kickstart`). The
  OS starts that service with the per-user default folders, so this path is taken only when the caller uses them too.
  A caller with `--config-dir`, `LATTICELOG_CONFIG_DIR` or `LATTICELOG_DATA_DIR` (tests, portable use) starts its own
  detached process instead of waking the user's service and waiting for an endpoint that never appears.
- **Not registered:** as a detached process. On Windows this is a ShellExecute that inherits no handles; on Unix,
  `nohup` with `/dev/null` streams. The client's stdio pipes must not leak into a process that outlives the shim.

When the service cannot be reached, the shim serves in-process and logs the reason on stderr. `--no-service` or
`LATTICELOG_NO_SERVICE=1` forces in-process serving.

If the service goes away mid-session, the shim keeps the client's session:

1. It reconnects, or falls back to in-process.
2. It replays `initialize` and `notifications/initialized`, and swallows the replayed answer.
3. It resends the requests that were in flight, once. Every tool is safe to repeat: the read tools, and
   `migrate_logs`, which is idempotent.

When stdin closes, the shim answers the requests already sent before it exits.

### Registration (per user, no administrator rights)

| OS | method | what is written | start / stop |
|---|---|---|---|
| Windows | `task` (default) | Task Scheduler task `LatticeLog`: logon trigger for the user, `LeastPrivilege`, restart on failure; action `latticelogw.exe service run` | `schtasks /Run`; stop over the line protocol |
| Windows | `run-key` | value `LatticeLog` under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` | started detached; stop over the line protocol |
| Linux | `systemd` | `~/.config/systemd/user/latticelog.service` (`Restart=on-failure`), enabled | `systemctl --user start` |
| macOS | `launchd` | `~/Library/LaunchAgents/dev.latticelog.service.plist` (`RunAtLoad`, `KeepAlive` on failure) | `launchctl bootstrap` / `kickstart` |

`latticelogw.exe` is a copy of the executable with its PE subsystem field set to GUI (like `pythonw.exe`), so the
service runs without a console window.

`setup` copies the new executable over the installed one. On Windows a running executable (an editor keeps
`latticelog.exe mcp` open) cannot be overwritten but can be renamed. So the old file is moved aside as
`latticelog.exe.old-<timestamp>`, and later setups (and `setup --uninstall`) delete those copies once nothing runs them.
`latticelogw.exe` is refreshed the same way.

`service install --dry-run` prints the plan and the files it would write, and changes nothing. `service uninstall` stops the service and removes the registration; the data folder stays.

### Health

`latticelog service status` (also `--format json` or `GET /v1/status`) shows:

- pid, version, uptime, port and registration;
- active and served MCP sessions;
- last refresh and last maintenance;
- disk use of the data folder, with indexes and migrated sources broken out;
- per source: kind, records, bytes, last indexed time, last time new records arrived, last error, retention;
- warnings and the last 50 errors.

When the service is not running, the same command shows the sources as they are on disk, with the last indexed time
taken from their checkpoints. The service log is `<data>/service/service.log`, rotated once at 5 MiB.
