#!/usr/bin/env sh
# Install LatticeLog once for this user on Linux or macOS (no sudo, nothing system-wide):
#   1. copies the single-file `latticelog` to ~/.local/bin;
#   2. registers the background service (systemd user unit / launchd agent, 127.0.0.1 only) and starts it;
#   3. adds LatticeLog to the MCP clients you choose (user-wide entries; existing files are backed up first).
# It runs `latticelog setup`, which does all of the above; this script only finds (or builds) the executable.
#
#   sh scripts/install.sh ./latticelog --clients claude-code,cursor
#   sh scripts/install.sh --build                  # build it from this checkout first (needs the .NET 10 SDK + clang)
#   sh scripts/install.sh --uninstall [--remove-data]
set -eu
here=$(cd "$(dirname "$0")" && pwd)
exe=""
if [ "${1:-}" = "--uninstall" ]; then
  shift
  cmd="$HOME/.local/bin/latticelog"; [ -x "$cmd" ] || cmd=latticelog
  exec "$cmd" setup --uninstall "$@"
fi
if [ "${1:-}" = "--build" ]; then
  shift
  case "$(uname -s)-$(uname -m)" in
    Linux-x86_64) rid=linux-x64 ;; Linux-aarch64) rid=linux-arm64 ;;
    Darwin-arm64) rid=osx-arm64 ;; Darwin-x86_64) rid=osx-x64 ;;
    *) echo "unsupported platform $(uname -s)-$(uname -m)" >&2; exit 1 ;;
  esac
  dotnet publish "$here/../src/LatticeLog.Cli/LatticeLog.Cli.csproj" -c Release -r "$rid" -p:PublishAot=true -o "$here/../artifacts/aot/$rid"
  exe="$here/../artifacts/aot/$rid/latticelog"
elif [ -n "${1:-}" ] && [ "${1#--}" = "$1" ]; then
  exe=$1; shift
fi
if [ -z "$exe" ]; then
  for c in "$here/latticelog" ./latticelog; do [ -x "$c" ] && { exe=$c; break; }; done
fi
[ -n "$exe" ] && [ -x "$exe" ] || { echo "latticelog not found: pass its path, or --build" >&2; exit 1; }
exec "$exe" setup "$@"
