# Smoke test of the single-file Native AOT latticelog.exe with no .NET available to it:
# - proves the file is native (no CLR header) and runs with dotnet removed from PATH and every DOTNET_* variable unset;
# - writes a Python local-mode source, registers its folder, then asks MCP (through the shim, which auto-starts the
#   background service) for list_sources and get_error_groups, and again in-process with --no-service;
# - shows `service status`, then stops the service.
# Everything lives in a temporary folder (LATTICELOG_CONFIG_DIR / LATTICELOG_DATA_DIR); nothing is registered.
#   pwsh scripts/smoke-aot.ps1 -Exe artifacts/aot/win-x64/latticelog.exe
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [switch]$NoService,
    # Only to exercise the flow with a self-contained trimmed build where no native linker is available.
    [switch]$SkipNativeCheck
)
$ErrorActionPreference = "Stop"
$Exe = (Resolve-Path $Exe).Path

function Fail($message) { Write-Host "SMOKE FAILED: $message" -ForegroundColor Red; exit 1 }

# 1. Native: a .NET assembly has a CLR runtime header (PE data directory 14); a Native AOT executable has none.
$bytes = [IO.File]::ReadAllBytes($Exe)
$pe = [BitConverter]::ToInt32($bytes, 0x3C)
$opt = $pe + 24
$magic = [BitConverter]::ToUInt16($bytes, $opt)
$dirs = if ($magic -eq 0x20B) { $opt + 112 } else { $opt + 96 }
$clr = [BitConverter]::ToUInt32($bytes, $dirs + 14 * 8)
if ($SkipNativeCheck) { Write-Host "native check skipped (-SkipNativeCheck): $Exe" -ForegroundColor Yellow }
else {
    if ($clr -ne 0) { Fail "$Exe has a CLR header: it is a .NET assembly, not a Native AOT executable" }
    if (Test-Path (Join-Path (Split-Path $Exe) "latticelog.dll")) { Fail "latticelog.dll next to the executable: not a single-file build" }
    Write-Host ("native single file: {0} ({1:N1} MB, no CLR header)" -f $Exe, ($bytes.Length / 1MB))
}

# 2. No .NET for anything started from here on.
$env:PATH = (($env:PATH -split ';') | Where-Object { $_ -and -not (Test-Path (Join-Path $_ 'dotnet.exe')) }) -join ';'
Get-ChildItem env: | Where-Object { $_.Name -like 'DOTNET_*' -or $_.Name -like 'COREHOST_*' } | ForEach-Object { Remove-Item "env:$($_.Name)" }
if (Get-Command dotnet -ErrorAction SilentlyContinue) { Fail "dotnet is still reachable on PATH" }

$work = Join-Path ([IO.Path]::GetTempPath()) ("latticelog-smoke-" + [guid]::NewGuid().ToString("N"))
$src = Join-Path $work "logs\smoke-api"
New-Item -ItemType Directory -Force (Join-Path $src "p-1"), (Join-Path $work "config") | Out-Null
$env:LATTICELOG_CONFIG_DIR = Join-Path $work "config"
$env:LATTICELOG_DATA_DIR = Join-Path $work "data"
try {
    # 3. A Python local-mode source, as latticelog.open(folder, service="smoke-api") writes it: 50 records, 5 errors.
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    [IO.File]::WriteAllText((Join-Path $src "latticelog.source.json"), '{"format":"latticelog.source","version":1,"service":"smoke-api","environment":"smoke","data_format":"latticelog.jsonl","data_version":1,"producer_id":"smoke"}', $utf8)
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add('{"type":"header","format":"latticelog.jsonl","version":1,"store_id":"0123456789abcdef0123456789abcdef"}')
    $lines.Add('{"type":"context","ref":1,"kind":"Resource","attributes":[["service.name","smoke-api"]]}')
    for ($i = 1; $i -le 50; $i++) {
        $t = [DateTimeOffset]::UtcNow.AddMinutes(-60 + $i).ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
        if ($i % 10 -eq 0) { $lines.Add("{""type"":""record"",""seq"":""$i"",""time"":""$t"",""severity_number"":17,""event"":""orders.failed"",""body"":""payment declined"",""attributes"":[[""n"",$i]],""exception"":{""type"":""PaymentDeclined"",""message"":""card $i""},""context"":1}") }
        else { $lines.Add("{""type"":""record"",""seq"":""$i"",""time"":""$t"",""severity_number"":9,""event"":""orders.ok"",""body"":""ok"",""attributes"":[[""n"",$i]],""context"":1}") }
    }
    [IO.File]::WriteAllText((Join-Path $src "p-1\latticelog-00000000000000000001.jsonl"), (($lines -join "`n") + "`n"), $utf8)
    & $Exe roots add (Join-Path $work "logs")
    if ($LASTEXITCODE -ne 0) { Fail "roots add exited $LASTEXITCODE" }

    $requests = @(
        '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"smoke","version":"1"}}}',
        '{"jsonrpc":"2.0","method":"notifications/initialized"}',
        '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"list_sources","arguments":{}}}',
        '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"get_error_groups","arguments":{"source":"smoke-api"}}}'
    )

    function Ask([string[]]$extra, [string]$expectServedBy) {
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $out = $requests | & $Exe mcp @extra
        if ($LASTEXITCODE -ne 0) { Fail "mcp exited $LASTEXITCODE" }
        $byId = @{}
        foreach ($l in $out) { if ($l.Trim().StartsWith("{")) { $m = $l | ConvertFrom-Json; if ($null -ne $m.id) { $byId[[string]$m.id] = $m } } }
        $list = $byId["2"].result.structuredContent
        $groups = $byId["3"].result.structuredContent
        if ($null -eq $list) { Fail "no list_sources answer: $out" }
        if ($list.served_by -ne $expectServedBy) { Fail "list_sources served_by '$($list.served_by)', expected '$expectServedBy'" }
        $s = $list.sources | Where-Object { $_.name -eq "smoke-api" }
        if ($null -eq $s -or $s.records -ne 50) { Fail "smoke-api not listed with 50 records: $($list | ConvertTo-Json -Depth 6 -Compress)" }
        if ($null -eq $groups) { Fail "no get_error_groups answer: $out" }
        $count = ($groups.groups | Measure-Object -Property count -Sum).Sum
        $text = $groups | ConvertTo-Json -Depth 10 -Compress
        if ($count -ne 5 -or -not $text.Contains("PaymentDeclined")) { Fail "get_error_groups: expected 5 PaymentDeclined errors, got $text" }
        Write-Host ("mcp {0}: list_sources + get_error_groups OK, served by {1} ({2:N0} ms)" -f ($extra -join ' '), $expectServedBy, $sw.Elapsed.TotalMilliseconds)
    }

    if (-not $NoService) {
        Ask @() "service"          # auto-starts the background service (detached, outlives this shim)
        Ask @() "service"          # second client: the running service answers
        & $Exe service status
        if ($LASTEXITCODE -ne 0) { Fail "service status exited $LASTEXITCODE" }
        & $Exe service stop
    }
    Ask @("--no-service") "in-process"
    Write-Host "SMOKE OK" -ForegroundColor Green
}
finally {
    & $Exe service stop *> $null
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
