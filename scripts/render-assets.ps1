# Renders the SVG sources in docs/assets to the image files other tools need: PNG for GitHub's social preview and
# article sites, BMP for the Windows installer's pages (installer/windows/assets). Uses a locally installed Edge or
# Chrome in headless mode; nothing is downloaded. Windows only for the BMP step (System.Drawing). The icon is not
# rendered here: it is src/LatticeLog.Cli/latticelog.ico, used by the executable and the installer, and drawn onto
# the installer pages.
#   pwsh scripts/render-assets.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $root 'docs/assets'
$installerAssets = Join-Path $root 'installer/windows/assets'

$browser = @(
    "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
    "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe",
    "$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
    '/usr/bin/google-chrome', '/usr/bin/chromium', '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome'
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $browser) { throw 'Edge or Chrome is needed to render the PNG files.' }
$renderProfile = Join-Path ([IO.Path]::GetTempPath()) ("latticelog-render-profile-" + [Guid]::NewGuid().ToString('N'))

# Renders one SVG to a PNG of the given size. -Transparent keeps the corners outside the SVG transparent (for icons).
function Export-Svg([string]$Svg, [string]$Png, [int]$W, [int]$H, [switch]$Transparent) {
    # Wrap the SVG in a page with no margins so the screenshot is exactly the image.
    $page = Join-Path ([IO.Path]::GetTempPath()) ("latticelog-render-" + [Guid]::NewGuid().ToString('N') + '.html')
    $uri = ([Uri]$Svg).AbsoluteUri
    $bg = if ($Transparent) { 'transparent' } else { '#121722' }
    Set-Content -Path $page -Encoding utf8 -Value "<!doctype html><html><body style='margin:0;background:$bg'><img src='$uri' width='$W' height='$H' style='display:block'></body></html>"
    try {
        $extra = if ($Transparent) { @('--default-background-color=00000000') } else { @() }
        # A profile of its own, so a browser that is already open does not take the request. msedge.exe can return before
        # the screenshot is written; Start-Process -Wait waits for the processes it started too.
        Remove-Item $Png -ErrorAction SilentlyContinue
        $argv = @('--headless=new', '--disable-gpu', '--hide-scrollbars', '--force-device-scale-factor=1', "--user-data-dir=`"$renderProfile`"") + $extra +
            @("--window-size=$W,$H", "--screenshot=`"$Png`"", ([Uri]$page).AbsoluteUri)
        Start-Process -FilePath $browser -ArgumentList $argv -Wait -WindowStyle Hidden
        if (-not (Test-Path $Png)) { throw "rendering $Svg failed" }
    } finally { Remove-Item $page -ErrorAction SilentlyContinue }
}

# svg file → png file, at the SVG's own size
$jobs = @(
    @{ Svg = 'social-preview.svg'; Png = 'social-preview.png'; W = 1280; H = 640 },
    @{ Svg = 'logo.svg';           Png = 'logo-512.png';       W = 512;  H = 512 }
)
foreach ($j in $jobs) {
    Export-Svg (Join-Path $assets $j.Svg) (Join-Path $assets $j.Png) $j.W $j.H
    Write-Host "$($j.Svg) -> $($j.Png)"
}

if (-not $IsWindows -and $PSVersionTable.PSEdition -eq 'Core') { Write-Host 'Skipping the installer images (Windows only).'; return }
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force $installerAssets | Out-Null
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("latticelog-assets-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $tmp | Out-Null
try {
    # The icon of the executable (its 256 px PNG entry) is drawn on top of the rendered SVG.
    $ico = [IO.File]::ReadAllBytes((Join-Path $root 'src/LatticeLog.Cli/latticelog.ico'))
    $iconPng = $null
    for ($i = 0; $i -lt [BitConverter]::ToUInt16($ico, 4); $i++) {
        $e = 6 + 16 * $i
        if ($ico[$e] -eq 0 -and $ico[$e + 1] -eq 0) {   # 0 means 256
            $len = [BitConverter]::ToUInt32($ico, $e + 8); $off = [BitConverter]::ToUInt32($ico, $e + 12)
            $iconPng = Join-Path $tmp 'icon-256.png'
            [IO.File]::WriteAllBytes($iconPng, $ico[$off..($off + $len - 1)])
        }
    }
    if (-not $iconPng) { throw 'latticelog.ico has no 256 px PNG image.' }
    $icon = [Drawing.Image]::FromFile($iconPng)

    # Installer pages: 24-bit BMP, the only format Windows Installer dialogs show.
    foreach ($j in @(@{ Svg = 'installer-dialog.svg'; Bmp = 'dialog.bmp'; W = 493; H = 312; X = 26;  Y = 40; S = 112 },
                     @{ Svg = 'installer-banner.svg'; Bmp = 'banner.bmp'; W = 493; H = 58;  X = 439; Y = 6;  S = 46 })) {
        $png = Join-Path $tmp ($j.Bmp + '.png')
        Export-Svg (Join-Path $assets $j.Svg) $png $j.W $j.H
        $src = [Drawing.Image]::FromFile($png)
        $bmp = New-Object Drawing.Bitmap $j.W, $j.H, ([Drawing.Imaging.PixelFormat]::Format24bppRgb)
        $g = [Drawing.Graphics]::FromImage($bmp)
        $g.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.DrawImage($src, 0, 0, $j.W, $j.H)
        $g.DrawImage($icon, $j.X, $j.Y, $j.S, $j.S)
        $g.Dispose(); $src.Dispose()
        $bmp.Save((Join-Path $installerAssets $j.Bmp), [Drawing.Imaging.ImageFormat]::Bmp); $bmp.Dispose()
        Write-Host "$($j.Svg) -> installer/windows/assets/$($j.Bmp)"
    }
    $icon.Dispose()
} finally { Remove-Item $tmp -Recurse -ErrorAction SilentlyContinue }
