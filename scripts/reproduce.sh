#!/usr/bin/env bash
# Reproduces tests, golden-file checks (two independent readers) and the baseline benchmark (P02-T18).
set -euo pipefail
cd "$(dirname "$0")/.."
EVENTS="${EVENTS:-100000}"
REPS="${REPS:-3}"

dotnet --version
dotnet test LatticeLog.sln -c Release
python3 -I tools/pyllog/llog.py verify-golden tests/GoldenFiles/v0.1 tests/GoldenFiles/v0.2
dotnet run -c Release --project research/LatticeLog.Lab -- baseline --events "$EVENTS" --reps "$REPS" --out research/results
echo "Results written to research/results/. Compare with reports/baseline-v1.md (hardware differs!)."
