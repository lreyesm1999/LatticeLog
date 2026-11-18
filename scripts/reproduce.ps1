# Reproduces tests, golden-file checks (two independent readers) and the baseline benchmark (P02-T18).
param([int]$Events = 100000, [int]$Reps = 3)
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')
dotnet --version
dotnet test LatticeLog.sln -c Release
if ($LASTEXITCODE -ne 0) { throw 'tests failed' }
python -I tools/pyllog/llog.py verify-golden tests/GoldenFiles/v0.1 tests/GoldenFiles/v0.2
if ($LASTEXITCODE -ne 0) { throw 'independent reader failed' }
dotnet run -c Release --project research/LatticeLog.Lab -- baseline --events $Events --reps $Reps --out research/results
dotnet run -c Release --project benchmarks/LatticeLog.Benchmarks -- latency --seconds 5 --warmup 1 --out research/results
# Public dataset experiment, only if Loghub-2.0 was downloaded (see research/dataset-manifest.yaml; never committed).
if (Test-Path research/data/loghub-2.0/zip) {
    dotnet run -c Release --project research/LatticeLog.Lab -- loghub --lines 200000 --out research/results
}
Write-Host 'Results written to research/results/. Compare with reports/ (hardware differs!).'
