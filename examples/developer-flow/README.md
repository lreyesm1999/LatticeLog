# Developer flow: logs by service name

A Python structlog service writes LatticeLog's format into `logs/demo-api`, an old structlog JSON log is migrated as
`legacy-demo`, and an MCP client asks about both **by name**. Specification: [`specs/local-sources.md`](../../specs/local-sources.md).

```bash
cd examples/developer-flow
python demo_api.py              # structlog service -> logs/demo-api (latticelog.open + one processor)
python make_legacy_log.py       # a traditional structlog JSON log -> old-logs/legacy-demo.log
python ask_mcp.py latticelog    # or: python ask_mcp.py dotnet ../../src/LatticeLog.Cli/bin/Release/net10.0/latticelog.dll
```

`latticelog.toml` here registers `logs` as a root and allows migration from `old-logs`, so nothing is added to your
per-user configuration. `ask_mcp.py` speaks JSON-RPC over stdio like an AI client: `list_sources`, `preview_migration`,
`migrate_logs` (with progress), `get_error_groups(source=...)` for each source and a `search_events` merged across both.
Set `LATTICELOG_DATA_DIR` to keep the demo's index and migrated store out of your data folder.

Output of a run (2026-11-19):

```text
tools: list_sources, search_events, get_event, get_trace, get_related_events, get_error_groups, get_context_pack, get_event_schema, get_overview, preview_migration, migrate_logs (writes)

> list_sources
   demo-api       python-local     213 records  ...\examples\developer-flow\logs\demo-api

> preview_migration(old-logs/legacy-demo.log)
   format: structlog json via StructuredJsonlImporter; 1000 lines; estimated store size 7544 bytes
   cannot keep: JSON number formatting (e.g. 1.50 vs 1.5) and key order inside nested objects are normalized.

> migrate_logs(old-logs/legacy-demo.log, source_name='legacy-demo')
   progress: 1,000 records imported (100% of new bytes)
   imported 1000 records (11 unparsed lines kept as text); 120049 bytes -> 7608 bytes; originals: read only; not modified, moved or deleted

> get_error_groups(source='demo-api')
   [demo-api] 8x inventory_call_failed __main__.UpstreamTimeout: inventory did not answer for order 3

> get_error_groups(source='legacy-demo')
   [legacy-demo] 20x charge_failed httpx.HTTPStatusError: Server error '503 Service Unavailable' for url 'https://payments.inter
```

To use it from your own AI client instead: `latticelog mcp install --client claude-code` in this folder.
