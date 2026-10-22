<#
Builds the Hushcue installer from a tools\publish.ps1 output folder with Inno Setup 6.

    powershell -ExecutionPolicy Bypass -File installer\build.ps1 -Version 1.2.3 [-PublishDir <dir>] [-OutputDir <dir>] [-Iscc <ISCC.exe>]

ISCC.exe is looked up in -Iscc, on PATH, then in the usual Inno Setup 6 install folders. Inno Setup does not need to be
installed system-wide: its installer can unpack a portable copy with
    innosetup-6.x.exe /VERYSILENT /PORTABLE=1 /CURRENTUSER /DIR=<folder>
#>
param(
    [Parameter(Mandatory)][string]$Version,
    [string]$PublishDir,
    [string]$OutputDir,
    [string]$Iscc
)
$ErrorActionPreference = "Stop"
# defaults set here, not in param(): Windows PowerShell 5.1 left $PSScriptRoot empty there
if (-not $PublishDir) { $PublishDir = Join-Path $PSScriptRoot "..\artifacts\publish\Hushcue" }
if (-not $OutputDir) { $OutputDir = Join-Path $PSScriptRoot "..\artifacts\installer" }

if (-not $Iscc) {
    $Iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
    if (-not $Iscc) {
        $Iscc = @(
            (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
            (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe"),
            (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe")
        ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    }
}
if (-not $Iscc -or -not (Test-Path $Iscc)) { throw "ISCC.exe (Inno Setup 6) not found; pass -Iscc" }
$PublishDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($PublishDir)
if (-not (Test-Path (Join-Path $PublishDir "Hushcue.exe"))) { throw "No Hushcue.exe in ${PublishDir}: run tools\publish.ps1 first" }
$OutputDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDir)

# Windows version resources take numbers only: 1.2.3-beta.1 -> 1.2.3
$fileVersion = ($Version -split '[-+]')[0]
& $Iscc /Q "/DAppVersion=$Version" "/DFileVersion=$fileVersion" "/DSourceDir=$PublishDir" "/DOutputDir=$OutputDir" `
    (Join-Path $PSScriptRoot "hushcue.iss")
if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }
$setup = Join-Path $OutputDir "Hushcue-$Version-setup.exe"
Write-Host ("Built {0} ({1:N0} MB)" -f $setup, ((Get-Item $setup).Length / 1MB))
