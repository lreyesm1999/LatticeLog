# Web console screenshots

Real screenshots of the console (`llog ui`) taken on 2026-11-21 from commit `4b61529` plus this branch, against an
**isolated** service instance and **demo data only**:

| file | what it shows | data |
|---|---|---|
| `console-sources.png` | the source explorer with five sources | `orders-api` (`examples/dotnet-console`), `checkout-web` (`examples/aspnetcore --smoke`), `demo-api` (`examples/developer-flow/demo_api.py`), `billing-legacy` (`make_legacy_log.py` + `--append 5000`, migrated) and `checkout-legacy` (below, migrated) |
| `console-search.png` | `checkout-legacy`, all time: the day's volume and the incident's errors in red | synthetic |
| `console-errors.png` | the same source filtered to errors: `httpx.ReadTimeout` between 14:05 and 14:35 | synthetic |

`checkout-legacy` is **synthetic**: one day of structlog JSON lines written by
[`scripts/screenshots/make_demo_day.py`](../../../scripts/screenshots/make_demo_day.py) (seeded, with an injected
14:05–14:35 UTC incident), then migrated with `latticelog migrate`. The numbers in the screenshots describe that demo
data, not a benchmark.

## Recipe

```bash
export LATTICELOG_CONFIG_DIR=$PWD/shot-cfg LATTICELOG_DATA_DIR=$PWD/shot-data   # never touch your real config
python scripts/screenshots/make_demo_day.py old-logs/checkout.log
latticelog allow add ./old-logs
latticelog migrate old-logs/checkout.log --name checkout-legacy
latticelog roots add ./logs          # a folder holding the example stores, if you want them listed too
latticelog service run --port 7499 & # in another terminal
python -I scripts/screenshots/capture_console.py "$(latticelog ui --no-open)" docs/assets/screenshots
latticelog service stop
```

The capture runs headless Edge at 1440×860 (scale 1.5), dark theme, `en-US`, UTC. Check every image for local paths or
user names before committing it.
