# Install LatticeLog once for this Windows user (no administrator rights, nothing system-wide):
#   1. copies the single-file latticelog.exe to %LOCALAPPDATA%\Programs\LatticeLog and adds it to your user PATH;
#   2. registers the background service (a Task Scheduler task at logon, 127.0.0.1 only) and starts it;
#   3. adds LatticeLog to the MCP clients you choose (user-wide entries; existing files are backed up first).
# It runs `latticelog setup`, which does all of the above; this script only finds (or builds) the executable.
#
#   pwsh scripts/install.ps1 -Exe .\latticelog.exe -Clients claude-desktop,claude-code
#   pwsh scripts/install.ps1 -Build                         # build it from this checkout first (scripts/publish-aot.ps1)
#   pwsh scripts/install.ps1 -Uninstall [-RemoveData]
param(
    [string]$Exe,
    [string[]]$Clients,              # claude-desktop, claude-code, cursor, vscode; omit to be asked; "none" for none
    [switch]$Build,
    [switch]$NoService,
    [switch]$NoPath,
    [ValidateSet("task", "run-key")][string]$Method = "task",
    [switch]$Uninstall,
    [switch]$RemoveData,
    [switch]$Yes
)
$ErrorActionPreference = "Stop"
if ($Uninstall) {
    $installed = Join-Path $env:LOCALAPPDATA "Programs\LatticeLog\latticelog.exe"
    $cmd = if ($Exe) { $Exe } elseif (Test-Path $installed) { $installed } else { "latticelog" }
    $a = @("setup", "--uninstall"); if ($RemoveData) { $a += "--remove-data" }
    & $cmd @a
    exit $LASTEXITCODE
}
if ($Build) {
    & (Join-Path $PSScriptRoot "publish-aot.ps1") -Rid win-x64
    $Exe = Join-Path $PSScriptRoot "..\artifacts\aot\win-x64\latticelog.exe"
}
if (-not $Exe) {
    foreach ($candidate in @((Join-Path $PSScriptRoot "latticelog.exe"), (Join-Path $PSScriptRoot "..\artifacts\aot\win-x64\latticelog.exe"), ".\latticelog.exe")) {
        if (Test-Path $candidate) { $Exe = $candidate; break }
    }
}
if (-not $Exe -or -not (Test-Path $Exe)) { throw "latticelog.exe not found: pass -Exe <path>, or -Build to build it from this checkout." }
$a = @("setup", "--method", $Method)
if ($Clients) { $a += @("--clients", ($Clients -join ",")) }
if ($NoService) { $a += "--no-service" }
if ($NoPath) { $a += "--no-path" }
if ($Yes) { $a += "--yes" }
& (Resolve-Path $Exe).Path @a
exit $LASTEXITCODE
