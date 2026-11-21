# Renders the SVG sources in docs/assets to the PNG files GitHub and article sites need (GitHub's social preview
# accepts PNG/JPG only). Uses a locally installed Edge or Chrome in headless mode; nothing is downloaded.
#   pwsh scripts/render-assets.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $root 'docs/assets'

$browser = @(
    "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
    "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe",
    "$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
    '/usr/bin/google-chrome', '/usr/bin/chromium', '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome'
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $browser) { throw 'Edge or Chrome is needed to render the PNG files.' }

# svg file → png file, at the SVG's own size
$jobs = @(
    @{ Svg = 'social-preview.svg'; Png = 'social-preview.png'; W = 1280; H = 640 },
    @{ Svg = 'logo.svg';           Png = 'logo-512.png';       W = 512;  H = 512 }
)
foreach ($j in $jobs) {
    $svg = Join-Path $assets $j.Svg
    $png = Join-Path $assets $j.Png
    # Wrap the SVG in a page with no margins so the screenshot is exactly the image.
    $page = Join-Path ([IO.Path]::GetTempPath()) ("latticelog-render-" + [Guid]::NewGuid().ToString('N') + '.html')
    $uri = ([Uri]$svg).AbsoluteUri
    Set-Content -Path $page -Encoding utf8 -Value "<!doctype html><html><body style='margin:0;background:#121722'><img src='$uri' width='$($j.W)' height='$($j.H)' style='display:block'></body></html>"
    try {
        & $browser --headless=new --disable-gpu --hide-scrollbars --force-device-scale-factor=1 `
            "--window-size=$($j.W),$($j.H)" "--screenshot=$png" ([Uri]$page).AbsoluteUri 2>$null | Out-Null
        if (-not (Test-Path $png)) { throw "rendering $($j.Svg) failed" }
        Write-Host "$($j.Svg) -> $($j.Png)"
    } finally { Remove-Item $page -ErrorAction SilentlyContinue }
}
