#!/usr/bin/env sh
# Install LatticeLog once for this user on Linux or macOS (no sudo, nothing system-wide):
#   1. copies the single-file `latticelog` to ~/.local/bin;
#   2. registers the background service (systemd user unit / launchd agent, 127.0.0.1 only) and starts it;
#   3. adds LatticeLog to the MCP clients you choose (user-wide entries; existing files are backed up first).
# It runs `latticelog setup`, which does all of the above; this script only finds, builds or downloads the executable.
#
# One line, nothing to clone (Linux x64: downloads the newest release from GitHub and checks it against SHA256SUMS):
#   curl -fsSL https://raw.githubusercontent.com/lreyesm1999/LatticeLog/main/scripts/install.sh | sh
#   curl -fsSL .../install.sh | sh -s -- --clients claude-code --yes        # arguments go to `latticelog setup`
#   curl -fsSL .../install.sh | LATTICELOG_VERSION=0.1.0 sh                 # a specific release
#
# From a checkout or a downloaded executable:
#   sh scripts/install.sh ./latticelog --clients claude-code,cursor
#   sh scripts/install.sh --build                  # build it from this checkout first (needs the .NET 10 SDK + clang)
#   sh scripts/install.sh --download [setup args]  # the release from GitHub even when a local build exists
#   sh scripts/install.sh --uninstall [--remove-data]
set -eu
repo=lreyesm1999/LatticeLog
exe=""
download=""
# Run as a file (sh scripts/install.sh) or piped from curl (then $0 is just "sh" and there is no checkout).
case "$0" in *install.sh) here=$(cd "$(dirname "$0")" && pwd) ;; *) here="" ;; esac

if [ "${1:-}" = "--uninstall" ]; then
  shift
  cmd="$HOME/.local/bin/latticelog"; [ -x "$cmd" ] || cmd=latticelog
  exec "$cmd" setup --uninstall "$@"
fi
if [ "${1:-}" = "--build" ]; then
  shift
  [ -n "$here" ] || { echo "--build needs a checkout: run scripts/install.sh from the repository" >&2; exit 1; }
  case "$(uname -s)-$(uname -m)" in
    Linux-x86_64) rid=linux-x64 ;; Linux-aarch64) rid=linux-arm64 ;;
    Darwin-arm64) rid=osx-arm64 ;; Darwin-x86_64) rid=osx-x64 ;;
    *) echo "unsupported platform $(uname -s)-$(uname -m)" >&2; exit 1 ;;
  esac
  dotnet publish "$here/../src/LatticeLog.Cli/LatticeLog.Cli.csproj" -c Release -r "$rid" -p:PublishAot=true -o "$here/../artifacts/aot/$rid"
  exe="$here/../artifacts/aot/$rid/latticelog"
elif [ "${1:-}" = "--download" ]; then
  shift; download=1
elif [ -n "${1:-}" ] && [ "${1#--}" = "$1" ]; then
  exe=$1; shift
fi
if [ -z "$exe" ] && [ -z "$download" ] && [ -z "${LATTICELOG_VERSION:-}" ] && [ -n "$here" ]; then
  for c in "$here/latticelog" ./latticelog; do
    if [ -f "$c" ] && [ -x "$c" ]; then exe=$c; echo "Using $c (--download for the release instead)."; break; fi
  done
fi

fetch() { # url file
  if command -v curl >/dev/null 2>&1; then curl -fsSL "$1" -o "$2"
  elif command -v wget >/dev/null 2>&1; then wget -q -O "$2" "$1"
  else echo "Downloading needs curl or wget." >&2; return 1; fi
}

work=""
cleanup() { [ -z "$work" ] || rm -rf "$work"; }
trap 'rc=$?; cleanup; exit $rc' EXIT   # keep the exit code (dash would otherwise report the trap's own)
trap 'cleanup; exit 130' INT TERM

if [ -z "$exe" ]; then
  case "$(uname -s)-$(uname -m)" in
    Linux-x86_64) asset=latticelog-linux-x64 ;;
    MINGW*|MSYS*|CYGWIN*) echo "On Windows use scripts/install.ps1 (PowerShell)." >&2; exit 1 ;;
    *) echo "There is no release build for $(uname -s)-$(uname -m) yet: clone the repository and run 'sh scripts/install.sh --build'." >&2; exit 1 ;;
  esac
  work=$(mktemp -d "${TMPDIR:-/tmp}/latticelog-install.XXXXXX")
  if [ -n "${LATTICELOG_VERSION:-}" ]; then
    base="https://github.com/$repo/releases/download/v${LATTICELOG_VERSION#v}"
  else
    # Like `llog update`: the newest stable release, or the newest pre-release while there is no stable one.
    api="https://api.github.com/repos/$repo/releases"
    fetch "$api/latest" "$work/releases.json" 2>/dev/null || fetch "$api?per_page=30" "$work/releases.json"
    url=$(grep -o "\"browser_download_url\" *: *\"[^\"]*/$asset\"" "$work/releases.json" | head -n 1 | sed 's/.*"\(https:[^"]*\)"$/\1/')
    [ -n "$url" ] || { echo "No release of $repo has $asset." >&2; exit 1; }
    base=${url%/*}
  fi
  echo "Downloading $base/$asset"
  fetch "$base/$asset" "$work/latticelog" || { echo "Could not download $asset from $base (is there such a release?)." >&2; exit 1; }
  fetch "$base/SHA256SUMS" "$work/SHA256SUMS"
  expected=$(awk -v f="$asset" '{ n = $2; sub(/^\*/, "", n) } n == f { print tolower($1); exit }' "$work/SHA256SUMS")
  [ -n "$expected" ] || { echo "SHA256SUMS has no line for $asset." >&2; exit 1; }
  if command -v sha256sum >/dev/null 2>&1; then actual=$(sha256sum "$work/latticelog" | cut -c1-64)
  else actual=$(shasum -a 256 "$work/latticelog" | cut -c1-64); fi
  if [ "$actual" != "$expected" ]; then
    echo "Checksum mismatch for $asset: expected $expected, got $actual. Nothing was installed." >&2; exit 1
  fi
  echo "Checksum verified (SHA-256 $(printf %.16s "$actual")...)."
  chmod +x "$work/latticelog"
  exe="$work/latticelog"
fi
[ -f "$exe" ] && [ -x "$exe" ] || { echo "latticelog not found: pass its path, --build or --download" >&2; exit 1; }

# Piped from curl, stdin is the script itself: let setup ask its questions on the terminal instead.
if [ ! -t 0 ] && (: </dev/tty) 2>/dev/null; then
  "$exe" setup "$@" </dev/tty
else
  "$exe" setup "$@"
fi
