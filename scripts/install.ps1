# Install LatticeLog once for this Windows user (no administrator rights, nothing system-wide):
#   1. copies the single-file latticelog.exe to %LOCALAPPDATA%\Programs\LatticeLog and adds it to your user PATH;
#   2. registers the background service (a Task Scheduler task at logon, 127.0.0.1 only) and starts it;
#   3. adds LatticeLog to the MCP clients you choose (user-wide entries; existing files are backed up first).
# It runs `latticelog setup`, which does all of the above; this script only finds, builds or downloads the executable.
#
# One line, nothing to clone (downloads the newest release from GitHub and checks it against SHA256SUMS):
#   irm https://raw.githubusercontent.com/lreyesm1999/LatticeLog/main/scripts/install.ps1 | iex
#
# From a checkout or a downloaded executable:
#   pwsh scripts/install.ps1 -Exe .\latticelog.exe -Clients claude-desktop,claude-code
#   pwsh scripts/install.ps1 -Build                         # build it from this checkout first (scripts/publish-aot.ps1)
#   pwsh scripts/install.ps1 -Download [-Version 0.1.0]     # the release from GitHub even when a local build exists
#   pwsh scripts/install.ps1 -Uninstall [-RemoveData]
# Works in Windows PowerShell 5.1 and PowerShell 7.
param(
    [string]$Exe,
    [string[]]$Clients,              # claude-desktop, claude-code, cursor, vscode; omit to be asked; "none" for none
    [switch]$Build,
    [switch]$Download,
    [string]$Version,                # with a download: this release (0.1.0 or v0.1.0) instead of the newest
    [switch]$NoService,
    [switch]$NoPath,
    [ValidateSet("task", "run-key")][string]$Method = "task",
    [switch]$DryRun,                 # print what setup would do; change nothing
    [switch]$Uninstall,
    [switch]$RemoveData,
    [switch]$Yes
)

# Everything runs inside functions so that `irm ... | iex` leaves no settings behind in your session
# and an error never closes your PowerShell window.
function Get-LatticeLogRelease([string]$Repo, [string]$Version) {
    $headers = @{ "User-Agent" = "latticelog-installer"; "Accept" = "application/vnd.github+json" }
    if ($Version) {
        $tag = if ($Version.StartsWith("v")) { $Version } else { "v$Version" }
        try { return Invoke-RestMethod -UseBasicParsing -Headers $headers "https://api.github.com/repos/$Repo/releases/tags/$tag" }
        catch { throw "No release $tag in $Repo ($($_.Exception.Message))." }
    }
    # Like `llog update`: the newest stable release, or the newest pre-release while there is no stable one.
    $list = Invoke-RestMethod -UseBasicParsing -Headers $headers "https://api.github.com/repos/$Repo/releases?per_page=30"
    $all = @(@($list) | Where-Object { -not $_.draft })
    $stable = @($all | Where-Object { -not $_.prerelease })
    $pool = if ($stable.Count -gt 0) { $stable } else { $all }
    if ($pool.Count -eq 0) { throw "No release found in $Repo." }
    return $pool[0]
}

function Save-LatticeLogRelease([string]$Version, [string]$Dir) {
    $repo = "lreyesm1999/LatticeLog"
    # Release builds exist for Windows x64 only; Windows on ARM runs them under x64 emulation.
    $arch = if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE }
    if ($arch -notin @("AMD64", "ARM64")) { throw "There is no release build for $arch; build from source (-Build)." }
    if ($arch -eq "ARM64") { Write-Host "Windows on ARM: installing the x64 build (it runs under emulation)." }
    $asset = "latticelog-win-x64.exe"

    $release = Get-LatticeLogRelease $repo $Version
    $exeUrl = ($release.assets | Where-Object { $_.name -eq $asset }).browser_download_url
    $sumsUrl = ($release.assets | Where-Object { $_.name -eq "SHA256SUMS" }).browser_download_url
    if (-not $exeUrl -or -not $sumsUrl) { throw "Release $($release.tag_name) has no $asset or SHA256SUMS." }
    $kind = if ($release.prerelease) { " (pre-release)" } else { "" }
    Write-Host "Downloading LatticeLog $($release.tag_name.TrimStart('v'))$kind from $($release.html_url)"

    $exe = Join-Path $Dir "latticelog.exe"
    $sums = Join-Path $Dir "SHA256SUMS"
    Invoke-WebRequest -UseBasicParsing $exeUrl -OutFile $exe
    Invoke-WebRequest -UseBasicParsing $sumsUrl -OutFile $sums

    $expected = $null
    foreach ($line in Get-Content $sums) {
        if ($line -match '^([0-9a-fA-F]{64})\s+\*?(.+)$' -and $Matches[2].Trim() -eq $asset) { $expected = $Matches[1].ToLowerInvariant(); break }
    }
    if (-not $expected) { throw "SHA256SUMS of $($release.tag_name) has no line for $asset." }
    $actual = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expected) { throw "Checksum mismatch for ${asset}: expected $expected, got $actual. Nothing was installed." }
    Write-Host "Checksum verified (SHA-256 $($actual.Substring(0, 16))...)."
    return $exe
}

function Install-LatticeLog {
    $ErrorActionPreference = "Stop"
    $ProgressPreference = "SilentlyContinue"   # Windows PowerShell downloads crawl while drawing a progress bar
    if ([Environment]::OSVersion.Platform -ne "Win32NT") { throw "This installer is for Windows; on Linux or macOS use scripts/install.sh." }
    # Windows PowerShell 5.1 may still default to TLS 1.0/1.1, which GitHub refuses.
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $fromFile = [bool]$PSScriptRoot             # empty when the script was piped into iex

    if ($Uninstall) {
        $installed = Join-Path $env:LOCALAPPDATA "Programs\LatticeLog\latticelog.exe"
        $cmd = if ($Exe) { $Exe } elseif (Test-Path $installed) { $installed } else { "latticelog" }
        $a = @("setup", "--uninstall"); if ($RemoveData) { $a += "--remove-data" }
        & $cmd @a
        return
    }
    if ($Build) {
        if (-not $fromFile) { throw "-Build needs a checkout: run scripts/install.ps1 from the repository." }
        & (Join-Path $PSScriptRoot "publish-aot.ps1") -Rid win-x64
        $Exe = Join-Path $PSScriptRoot "..\artifacts\aot\win-x64\latticelog.exe"
    }
    if (-not $Exe -and -not $Download -and -not $Version -and $fromFile) {
        foreach ($candidate in @((Join-Path $PSScriptRoot "latticelog.exe"), (Join-Path $PSScriptRoot "..\artifacts\aot\win-x64\latticelog.exe"), ".\latticelog.exe")) {
            if (Test-Path $candidate) { $Exe = $candidate; Write-Host "Using $((Resolve-Path $candidate).Path) (-Download for the release instead)."; break }
        }
    }

    $work = $null
    try {
        if (-not $Exe) {
            $work = Join-Path ([IO.Path]::GetTempPath()) ("latticelog-install-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
            New-Item -ItemType Directory $work | Out-Null
            $Exe = Save-LatticeLogRelease $Version $work
        }
        if (-not (Test-Path $Exe)) { throw "latticelog.exe not found: $Exe" }
        $a = @("setup", "--method", $Method)
        if ($Clients) { $a += @("--clients", ($Clients -join ",")) }
        if ($NoService) { $a += "--no-service" }
        if ($NoPath) { $a += "--no-path" }
        if ($DryRun) { $a += "--dry-run" }
        if ($Yes) { $a += "--yes" }
        & (Resolve-Path $Exe).Path @a      # not captured: setup asks its questions on this console
    }
    finally {
        if ($work) { Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue }
    }
}

# The exit code is setup's own; Install-LatticeLog writes to the console only.
try { Install-LatticeLog; $code = $LASTEXITCODE }
catch { Write-Host "LatticeLog install failed: $($_.Exception.Message)" -ForegroundColor Red; $code = 1 }
if ($PSCommandPath) { exit $code }   # a script file: report the exit code; piped into iex: never close the window
