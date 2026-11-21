# Builds the single-file Native AOT latticelog executable (no .NET needed to run it).
# Needs the .NET 10 SDK and, on Windows, Visual Studio 2022 Build Tools with the "Desktop development with C++"
# workload (the native linker); on Linux clang and zlib1g-dev; on macOS the Xcode command line tools.
#   pwsh scripts/publish-aot.ps1                 # win-x64 -> artifacts/aot/win-x64/latticelog.exe
#   pwsh scripts/publish-aot.ps1 -Rid linux-x64  # on Linux
param(
    [string]$Rid = $(if ($IsLinux) { "linux-x64" } elseif ($IsMacOS) { "osx-arm64" } else { "win-x64" }),
    [string]$Output = $(Join-Path $PSScriptRoot "..\artifacts\aot\$Rid")
)
$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "..\src\LatticeLog.Cli\LatticeLog.Cli.csproj"
dotnet publish $project -c Release -r $Rid -p:PublishAot=true -o $Output
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
$exe = Join-Path $Output $(if ($Rid -like "win-*") { "latticelog.exe" } else { "latticelog" })
Write-Host ("Built {0} ({1:N1} MB)." -f (Resolve-Path $exe), ((Get-Item $exe).Length / 1MB))
Write-Host "Install it: $exe setup    (or scripts/install.ps1)"
