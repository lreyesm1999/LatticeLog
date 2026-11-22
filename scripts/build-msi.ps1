# Builds the Windows installer (latticelog-win-x64.msi) around a Native AOT latticelog.exe.
#   pwsh scripts/build-msi.ps1 -Exe artifacts\aot\win-x64\latticelog.exe -Version 0.1.0-alpha.7
#   pwsh scripts/build-msi.ps1 -Exe .\latticelog-win-x64.exe -Version 0.1.0-alpha.7 -OutDir dist
# Needs Windows and the .NET SDK; the WiX toolset comes from NuGet on the first build (installer/windows).
# The release workflow runs this for every tag; the MSI is listed in SHA256SUMS like the executables.
param(
    [Parameter(Mandatory)][string]$Exe,
    [Parameter(Mandatory)][string]$Version,   # the release version, as in the tag without its v (0.1.0-alpha.7)
    [string]$OutDir = 'artifacts/msi'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'installer/windows/LatticeLog.Installer.wixproj'
$Exe = (Resolve-Path $Exe).Path
if (-not [IO.Path]::IsPathRooted($OutDir)) { $OutDir = Join-Path $root $OutDir }

# Windows Installer versions are numeric (major.minor.build, plus a fourth field it does not compare).
# 0.1.0-alpha.7 → 0.1.0.7; 0.1.0 → 0.1.0.0.
if ($Version -notmatch '^v?(\d+)\.(\d+)\.(\d+)(?:-[0-9A-Za-z]+(?:\.(\d+))?)?(?:\+.*)?$') {
    throw "Version '$Version' is not major.minor.patch[-label.N]."
}
$msiVersion = '{0}.{1}.{2}.{3}' -f $Matches[1], $Matches[2], $Matches[3], ($(if ($Matches[4]) { $Matches[4] } else { 0 }))

# The license page shows LICENSE, converted to RTF (the format the installer's license page reads).
$obj = Join-Path $root 'installer/windows/obj'
New-Item -ItemType Directory -Force $obj | Out-Null
$rtf = Join-Path $obj 'License.rtf'
$text = (Get-Content (Join-Path $root 'LICENSE') -Raw) -replace '\r\n', "`n"
$body = ($text.TrimEnd() -split "`n" | ForEach-Object { ($_ -replace '\\', '\\' -replace '\{', '\{' -replace '\}', '\}') + '\par' }) -join "`r`n"
Set-Content -Path $rtf -Encoding ascii -Value ("{\rtf1\ansi\deff0{\fonttbl{\f0 Segoe UI;}}\fs18`r`n" + $body + "`r`n}")

Write-Host "Building the MSI for LatticeLog $Version (installer version $msiVersion) from $Exe"
dotnet build $project -c Release -nologo -v:minimal `
    "-p:ProductVersion=$msiVersion" "-p:ExePath=$Exe" "-p:LicenseRtf=$rtf" "-p:OutputPath=$OutDir\"
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed ($LASTEXITCODE)" }
$msi = Join-Path $OutDir 'latticelog-win-x64.msi'
if (-not (Test-Path $msi)) { throw "Expected $msi" }
Get-ChildItem $OutDir -Filter *.wixpdb | Remove-Item
Write-Host "Built $msi ($([math]::Round((Get-Item $msi).Length / 1MB, 1)) MB)"
