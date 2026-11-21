#!/usr/bin/env sh
# Runs the commands the README and docs/guides/getting-started.md show, against a fresh folder, and fails if any of
# them fails or prints something other than what the docs promise. CI runs it after the build; run it locally with
#   dotnet build LatticeLog.sln -c Release && sh scripts/check-examples.sh
set -eu

root="$(cd "$(dirname "$0")/.." && pwd)"
work="${1:-$(mktemp -d)}"
mkdir -p "$work"
cd "$work"
echo "working in $work"

cli() { dotnet run -c Release --no-build --project "$root/src/LatticeLog.Cli" -- "$@"; }
expect() { # expect <text> <file>: the docs show this text, so the command must print it
  if ! grep -q -- "$1" "$2"; then echo "expected '$1' in the output:"; cat "$2"; exit 1; fi
}

echo "== getting started: demo workload with an injected incident"
dotnet run -c Release --no-build --project "$root/examples/dotnet-console" -- demo-store 2000 > demo.txt
cat demo.txt
expect "ack=FsyncConfirmed, dropped=0" demo.txt

cli errors demo-store --top 3 --format json > errors.json
expect '"exhaustive": "yes"' errors.json
expect '"event": "order.failed"' errors.json
expect '"exception_type": "System.TimeoutException"' errors.json

cli query demo-store --where "level >= ERROR" --limit 3 --explain > query.txt 2>&1   # --explain writes to stderr
expect "pruning: severity>=ERROR" query.txt

cli stats demo-store --by event > stats.txt 2>&1
expect "from footer indexes, no block decoded" stats.txt

ref="$(grep -o 'llog://[0-9a-f]*/e/[0-9]*' errors.json | head -n 1)"
cli event demo-store "$ref" --format json > event.json
expect '"type": "System.TimeoutException"' event.json

trace="$(grep '^incident trace: ' demo.txt | cut -d' ' -f3)"
cli trace demo-store --trace-id "$trace" --format markdown > trace.md
expect "inventory.stock.timeout" trace.md

cli context demo-store --where "level >= WARN" --budget-bytes 16000 --format json > context.json
expect '"truncated"' context.json

cli verify demo-store

echo "== ASP.NET Core example (in-process self-check)"
dotnet run -c Release --no-build --project "$root/examples/aspnetcore" -- "$work/aspnet-store" --smoke > aspnet.txt   # absolute: dotnet run starts web apps in their project folder
cat aspnet.txt
expect "dropped=0" aspnet.txt
cli errors "$work/aspnet-store" --format json > aspnet-errors.json
expect '"event": "order.failed"' aspnet-errors.json
cli verify "$work/aspnet-store"

echo "== export round trip"
cli export demo-store --format llog-jsonl --output demo.jsonl
test "$(grep -c '^{"type":"record"' demo.jsonl)" -gt 8000

echo "all documented examples passed"
