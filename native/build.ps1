<#
Builds native\bin\hushcue_ct2.dll and gathers the runtime DLLs it needs into %LOCALAPPDATA%\Hushcue\runtime.

    powershell -ExecutionPolicy Bypass -File native\build.ps1 [-SitePackages <python site-packages>]

Needs Visual Studio 2022 Build Tools (C++ workload), CMake and git. The CTranslate2 binaries come from a Python
environment with the ctranslate2 4.8.2 wheel installed (by default SubGen's venv), together with the
nvidia-cublas-cu12 and nvidia-cudnn-cu12 wheels.
#>
param(
    [string]$SitePackages = (Join-Path $PSScriptRoot "..\..\subgen\.venv\Lib\site-packages"),
    [string]$Ct2Version = "4.8.2"
)
$ErrorActionPreference = "Stop"

$root = $PSScriptRoot
$deps = Join-Path $root "deps"
$build = Join-Path $root "build"
$bin = Join-Path $root "bin"
$runtime = Join-Path $env:LOCALAPPDATA "Hushcue\runtime"

# --- CTranslate2 headers, same version as the DLL
$ct2Source = Join-Path $deps "CTranslate2"
if (-not (Test-Path $ct2Source)) {
    git clone --quiet --depth 1 --branch "v$Ct2Version" https://github.com/OpenNMT/CTranslate2.git $ct2Source
    if ($LASTEXITCODE -ne 0) { throw "git clone failed" }
}
$ct2Dll = Join-Path $SitePackages "ctranslate2\ctranslate2.dll"
if (-not (Test-Path $ct2Dll)) { throw "ctranslate2.dll not found in $SitePackages" }
$installed = Get-ChildItem $SitePackages -Directory -Filter "ctranslate2-*.dist-info" | Select-Object -First 1
if (-not $installed -or $installed.Name -ne "ctranslate2-$Ct2Version.dist-info") {
    throw "Expected ctranslate2 $Ct2Version in $SitePackages, found $($installed.Name)"
}

# --- MSVC tools
$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw "Visual Studio 2022 Build Tools with the C++ workload are not installed" }
$msvc = Get-ChildItem (Join-Path $vs "VC\Tools\MSVC") -Directory | Sort-Object Name | Select-Object -Last 1
$tools = Join-Path $msvc.FullName "bin\Hostx64\x64"
$cmake = (Get-Command cmake -ErrorAction SilentlyContinue).Source
if (-not $cmake) { $cmake = Join-Path $env:ProgramFiles "CMake\bin\cmake.exe" }

# --- import library from the DLL's exports
New-Item -ItemType Directory -Force $build | Out-Null
$def = Join-Path $build "ctranslate2.def"
$lib = Join-Path $build "ctranslate2.lib"
$exports = & (Join-Path $tools "dumpbin.exe") /nologo /exports $ct2Dll
$names = foreach ($line in $exports) {
    if ($line -match '^\s+\d+\s+[0-9A-F]+\s+[0-9A-F]{8}\s+(\S+)') { $Matches[1] }
}
if ($names.Count -lt 100) { throw "Found only $($names.Count) exports in $ct2Dll" }
Set-Content -Path $def -Encoding ascii -Value (@("LIBRARY ctranslate2", "EXPORTS") + ($names | ForEach-Object { "    $_" }))
& (Join-Path $tools "lib.exe") /nologo /machine:x64 /def:$def /out:$lib
if ($LASTEXITCODE -ne 0) { throw "lib.exe failed" }

# --- build the bridge
& $cmake -S (Join-Path $root "hushcue_ct2") -B $build -G "Visual Studio 17 2022" -A x64 `
    "-DCT2_INCLUDE_DIR=$(Join-Path $ct2Source 'include')" "-DCT2_IMPORT_LIB=$lib"
if ($LASTEXITCODE -ne 0) { throw "cmake configure failed" }
& $cmake --build $build --config Release
if ($LASTEXITCODE -ne 0) { throw "build failed" }
New-Item -ItemType Directory -Force $bin | Out-Null
Copy-Item (Join-Path $build "Release\hushcue_ct2.dll") $bin -Force

# --- runtime DLLs: CTranslate2 (+ its OpenMP and cuDNN), cuBLAS, cuDNN
New-Item -ItemType Directory -Force $runtime | Out-Null
$sources = @(Join-Path $SitePackages "ctranslate2") + `
    @(Get-ChildItem (Join-Path $SitePackages "nvidia") -Directory | ForEach-Object { Join-Path $_.FullName "bin" } | Where-Object { Test-Path $_ })
foreach ($dir in $sources) {
    Get-ChildItem $dir -Filter *.dll | Copy-Item -Destination $runtime -Force
}
foreach ($required in "ctranslate2.dll", "cublas64_12.dll", "cudnn64_9.dll") {
    if (-not (Test-Path (Join-Path $runtime $required))) { throw "Missing $required (install the nvidia-cublas-cu12 and nvidia-cudnn-cu12 wheels)" }
}
Write-Host "Built $bin\hushcue_ct2.dll; runtime DLLs in $runtime"
