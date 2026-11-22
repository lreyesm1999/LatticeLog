# Installs the MSI silently with every feature, checks what `latticelog setup` did, uninstalls it and checks that it
# is all gone. For CI runners (installer.yml, release.yml): it changes the current user's PATH, Task Scheduler and
# MCP client configs, so do not run it on your own machine.
#   pwsh scripts/test-msi.ps1 -Msi dist\latticelog-win-x64.msi -Version 0.1.0-alpha.7
param(
    [Parameter(Mandatory)][string]$Msi,
    [Parameter(Mandatory)][string]$Version,
    [string]$LogDir = $env:RUNNER_TEMP
)
$ErrorActionPreference = 'Stop'
if (-not $LogDir) { $LogDir = [IO.Path]::GetTempPath() }
$Msi = (Resolve-Path $Msi).Path
$dir = Join-Path $env:LOCALAPPDATA 'Programs\LatticeLog'
$exe = Join-Path $dir 'latticelog.exe'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'LatticeLog Console.lnk'
$cursorConfig = Join-Path $env:USERPROFILE '.cursor\mcp.json'
$failures = [Collections.Generic.List[string]]::new()
function Check([string]$what, [bool]$ok) {
    Write-Host ("  [{0}] {1}" -f $(if ($ok) { 'ok' } else { 'FAIL' }), $what)
    if (-not $ok) { $failures.Add($what) }
}
function UserPathHas([string]$d) {
    ([Environment]::GetEnvironmentVariable('Path', 'User') -split ';') | Where-Object { $_.TrimEnd('\') -ieq $d.TrimEnd('\') }
}
function TaskExists { schtasks /Query /TN LatticeLog 2>$null | Out-Null; $LASTEXITCODE -eq 0 }
function Invoke-Msiexec([string[]]$argv, [string]$log) {
    $p = Start-Process msiexec -ArgumentList ($argv + @('/qn', '/l*v', "`"$log`"")) -Wait -PassThru
    # What `latticelog setup` printed, copied into the installer log by WixQuietExec.
    # Printed with Write-Host so that only the exit code is this function's output.
    if (Test-Path $log) { Get-Content $log -Encoding Unicode | Select-String 'WixQuietExec64:' | ForEach-Object { Write-Host ('    ' + $_.Line.Trim()) } }
    return $p.ExitCode
}

Write-Host "Install (all features): $Msi"
$code = Invoke-Msiexec @('/i', "`"$Msi`"", 'ADDLOCAL=ALL') (Join-Path $LogDir 'msi-install.log')
Check "msiexec /i exit code 0 (got $code)" ($code -eq 0)
Check "$exe exists" (Test-Path $exe)
if (Test-Path $exe) {
    $reported = & $exe --version
    Check "latticelog --version is '$Version' (got '$reported')" ($reported -eq "latticelog $Version")
}
Check 'llog.cmd and latticelogw.exe were created by setup' ((Test-Path (Join-Path $dir 'llog.cmd')) -and (Test-Path (Join-Path $dir 'latticelogw.exe')))
Check 'install folder is on the user PATH' ([bool](UserPathHas $dir))
Check 'Task Scheduler task LatticeLog exists' (TaskExists)
Check 'Start menu shortcut exists' (Test-Path $shortcut)
Check 'Cursor config has the latticelog MCP server' ((Test-Path $cursorConfig) -and ((Get-Content $cursorConfig -Raw) -match '"latticelog"'))
if (Test-Path $exe) {
    & $exe service status | Out-Host
    Check 'latticelog service status exits 0' ($LASTEXITCODE -eq 0)
}

Write-Host "Uninstall"
$code = Invoke-Msiexec @('/x', "`"$Msi`"") (Join-Path $LogDir 'msi-uninstall.log')
Check "msiexec /x exit code 0 (got $code)" ($code -eq 0)
Start-Sleep -Seconds 2
Check "$exe is gone" (-not (Test-Path $exe))
Check 'llog.cmd and latticelogw.exe are gone' (-not (Test-Path (Join-Path $dir 'llog.cmd')) -and -not (Test-Path (Join-Path $dir 'latticelogw.exe')))
Check 'install folder is off the user PATH' (-not (UserPathHas $dir))
Check 'Task Scheduler task is gone' (-not (TaskExists))
Check 'Start menu shortcut is gone' (-not (Test-Path $shortcut))
Check 'Cursor config no longer has latticelog' (-not ((Test-Path $cursorConfig) -and ((Get-Content $cursorConfig -Raw) -match '"latticelog"')))

if ($failures.Count -gt 0) { throw "$($failures.Count) check(s) failed: $($failures -join '; ')" }
Write-Host 'MSI install and uninstall: all checks passed.'
# The last schtasks query fails on purpose (the task is gone); its exit code must not become this script's.
exit 0
