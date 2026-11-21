#!/usr/bin/env sh
# Smoke test of the single-file Native AOT `latticelog` with no .NET available to it (see smoke-aot.ps1): runs
# list_sources and get_error_groups through `latticelog mcp` (auto-started background service) and in-process.
# Needs only a POSIX shell, grep and coreutils, so it also runs in a bare container such as debian:bookworm-slim.
#   sh scripts/smoke-aot.sh artifacts/aot/linux-x64/latticelog
set -eu
exe=$(cd "$(dirname "$1")" && pwd)/$(basename "$1")
fail() { echo "SMOKE FAILED: $*" >&2; exit 1; }

[ -x "$exe" ] || fail "$exe is not executable"
[ ! -e "$(dirname "$exe")/latticelog.dll" ] || fail "latticelog.dll next to the executable: not a single-file build"
work=$(mktemp -d)
trap '"$exe" service stop >/dev/null 2>&1 || true; rm -rf "$work"' EXIT
size=$(wc -c < "$exe")

# No .NET for anything started from here on. A PATH folder that holds dotnet is replaced by a folder of links to
# everything else in it: on GitHub's Ubuntu runners dotnet sits in /usr/bin next to coreutils, so dropping the whole
# folder would also hide wc, mktemp and the rest.
unset DOTNET_ROOT DOTNET_HOST_PATH DOTNET_ROOT_X64 2>/dev/null || true
newpath=""
n=0
old_ifs=$IFS
IFS=:
for d in $PATH; do
  [ -n "$d" ] || continue
  if [ -e "$d/dotnet" ]; then
    n=$((n + 1))
    shadow="$work/path-$n"
    mkdir -p "$shadow"
    for f in "$d"/* "$d"/.[!.]*; do
      [ -e "$f" ] || continue
      name=${f##*/}
      [ "$name" = dotnet ] || ln -s "$f" "$shadow/$name"
    done
    newpath="$newpath${newpath:+:}$shadow"
  else
    newpath="$newpath${newpath:+:}$d"
  fi
done
IFS=$old_ifs
PATH=$newpath
export PATH
if command -v dotnet >/dev/null 2>&1; then fail "dotnet is still reachable on PATH"; fi
echo "native single file: $exe ($size bytes), dotnet not on PATH"
export LATTICELOG_CONFIG_DIR="$work/config" LATTICELOG_DATA_DIR="$work/data"
src="$work/logs/smoke-api"
mkdir -p "$src/p-1" "$LATTICELOG_CONFIG_DIR"
printf '%s' '{"format":"latticelog.source","version":1,"service":"smoke-api","environment":"smoke","data_format":"latticelog.jsonl","data_version":1,"producer_id":"smoke"}' > "$src/latticelog.source.json"
f="$src/p-1/latticelog-00000000000000000001.jsonl"
{
  echo '{"type":"header","format":"latticelog.jsonl","version":1,"store_id":"0123456789abcdef0123456789abcdef"}'
  echo '{"type":"context","ref":1,"kind":"Resource","attributes":[["service.name","smoke-api"]]}'
  i=1
  while [ $i -le 50 ]; do
    t=$(printf '2026-01-01T00:%02d:00.000Z' $((i % 60)))
    if [ $((i % 10)) -eq 0 ]; then
      echo "{\"type\":\"record\",\"seq\":\"$i\",\"time\":\"$t\",\"severity_number\":17,\"event\":\"orders.failed\",\"body\":\"payment declined\",\"attributes\":[[\"n\",$i]],\"exception\":{\"type\":\"PaymentDeclined\",\"message\":\"card $i\"},\"context\":1}"
    else
      echo "{\"type\":\"record\",\"seq\":\"$i\",\"time\":\"$t\",\"severity_number\":9,\"event\":\"orders.ok\",\"body\":\"ok\",\"attributes\":[[\"n\",$i]],\"context\":1}"
    fi
    i=$((i + 1))
  done
} > "$f"
"$exe" roots add "$work/logs"

ask() { # $1 = expected served_by, rest = extra args
  expect=$1; shift
  out=$(printf '%s\n' \
    '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"smoke","version":"1"}}}' \
    '{"jsonrpc":"2.0","method":"notifications/initialized"}' \
    '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"list_sources","arguments":{}}}' \
    '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"get_error_groups","arguments":{"source":"smoke-api"}}}' \
    | "$exe" mcp "$@")
  list=$(printf '%s\n' "$out" | grep '"id":2')
  groups=$(printf '%s\n' "$out" | grep '"id":3')
  printf '%s' "$list" | grep -q "\"served_by\":\"$expect\"" || fail "list_sources not served by $expect: $list"
  printf '%s' "$list" | grep -q '"name":"smoke-api"' || fail "smoke-api not listed: $list"
  printf '%s' "$list" | grep -q '"records":50' || fail "smoke-api does not have 50 records: $list"
  printf '%s' "$groups" | grep -q 'PaymentDeclined' || fail "get_error_groups has no PaymentDeclined group: $groups"
  printf '%s' "$groups" | grep -q '"count":5' || fail "get_error_groups does not count 5 errors: $groups"
  echo "mcp $*: list_sources + get_error_groups OK, served by $expect"
}

if [ "${2:-}" != "--no-service" ]; then
  ask service
  ask service
  "$exe" service status
  "$exe" service stop
fi
ask in-process --no-service
echo "SMOKE OK"
