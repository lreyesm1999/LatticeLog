# Investigating an incident with an agent

What does a coding agent actually do with LatticeLog's MCP server, and what does it get back? This page answers that
with a scripted client, [`examples/developer-flow/ask_mcp.py`](../../examples/developer-flow/ask_mcp.py), which speaks
JSON-RPC over stdio exactly like Claude Code or Cursor do, so the run is reproducible without any AI service.

## The scenario

- `demo-api`, a Python service using **structlog**, writes LatticeLog's format into `logs/demo-api` with one added
  processor ([`demo_api.py`](../../examples/developer-flow/demo_api.py)). Its calls to an inventory service sometimes
  time out.
- `legacy-demo` is an *old* service that left a traditional structlog JSON file behind
  ([`make_legacy_log.py`](../../examples/developer-flow/make_legacy_log.py)), including Python tracebacks and a few
  lines that are not JSON.
- [`latticelog.toml`](../../examples/developer-flow/latticelog.toml) registers `logs` as a root and allows migration
  from `old-logs`, so nothing is added to your per-user configuration.

## Run it

```bash
cd examples/developer-flow
pip install structlog
python demo_api.py
python make_legacy_log.py
python ask_mcp.py dotnet ../../src/LatticeLog.Cli/bin/Release/net10.0/latticelog.dll   # or: python ask_mcp.py latticelog
```

Set `LATTICELOG_CONFIG_DIR` and `LATTICELOG_DATA_DIR` to scratch folders to keep the demo's index and migrated store
out of your real data folder.

## What the agent sees (real run, 2026-11-21)

```text
tools: list_sources, search_events, get_event, get_trace, get_related_events, get_error_groups, get_context_pack,
       get_event_schema, get_overview, preview_migration, migrate_logs (writes)

> list_sources
   demo-api       python-local     213 records  …/examples/developer-flow/logs/demo-api

> preview_migration(old-logs/legacy-demo.log)
   format: structlog json via StructuredJsonlImporter; 1000 lines; estimated store size 7553 bytes
   cannot keep: JSON number formatting (e.g. 1.50 vs 1.5) and key order inside nested objects are normalized.

> migrate_logs(old-logs/legacy-demo.log, source_name='legacy-demo')
   progress: 1,000 records imported (100% of new bytes)
   imported 1000 records (11 unparsed lines kept as text); 120049 bytes -> 7619 bytes;
   originals: read only; not modified, moved or deleted

> get_error_groups(source='demo-api')
   [demo-api] 8x inventory_call_failed __main__.UpstreamTimeout: inventory did not answer for order 3

> get_error_groups(source='legacy-demo')
   [legacy-demo] 20x charge_failed httpx.HTTPStatusError: Server error '503 Service Unavailable' for url 'https://payments.inter…

> search_events(source=['demo-api','legacy-demo'], where="level >= ERROR", limit=4, newest_first=true)
   2026-11-21T17:42:42.332835000Z [demo-api] inventory_call_failed __main__.UpstreamTimeout
   …
```

(The script prints a short summary of each JSON response; paths shortened with `…`.)

## What to notice

1. **Sources by name.** The agent never needed a path or a format. `list_sources` came first, then every call used
   `source='demo-api'`.
2. **Migration is explicit and previewed.** `preview_migration` said what would *not* survive (number formatting,
   nested key order) before anything was written. The 11 lines that were not JSON were kept as text, not dropped.
3. **Real exception types.** structlog's exceptions keep their Python type (`__main__.UpstreamTimeout`,
   `httpx.HTTPStatusError`), so grouping is by type, not by a string that happens to contain one.
4. **Coverage in every answer.** Each JSON response carries `coverage` (`truncated`, `partial_results`, `sampling`,
   `exhaustive`). An agent can tell "there were no other errors" from "I was only shown 20".
5. **No inference.** Groups are fingerprints, not causes. Correlating the inventory timeouts with the payment 503s is
   the agent's (or your) reasoning, and each conclusion can cite the `llog://` refs it relied on.
6. **Untrusted text is labelled.** Responses list which fields contain text written by applications. A log line that
   says "ignore previous instructions" stays data.

## Doing it in your editor

After [registering the MCP server](mcp.md), prompts that work well:

- "List my log sources, then show the error groups for `demo-api` and one full event per group."
- "Get the trace of the newest `inventory_call_failed` event and summarise the timeline. Cite the refs."
- "Preview migrating `old-logs/legacy-demo.log`. Don't migrate it yet."
- "Build a context pack for `level >= WARN` in the last hour with a 16 KB budget and tell me what was left out."

The last one is worth asking: the pack states what it omitted, so the agent can ask for more instead of guessing.
