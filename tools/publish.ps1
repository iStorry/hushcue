<#
Publishes the Hushcue app as a self-contained win-x64 folder, ready to zip or to package with installer\hushcue.iss.

    powershell -ExecutionPolicy Bypass -File tools\publish.ps1 [-Output <dir>] [-Version 1.2.3]
        [-SitePackages <python site-packages>] [-BundleFfmpeg <ffmpeg folder>] [-NoReadyToRun] [-SmokeTest]

Run native\build.ps1 first (it builds native\bin\hushcue_ct2.dll). The folder gets:
    Hushcue.exe + .NET + Windows App SDK   self-contained, ReadyToRun (see docs/shipping.md for the measurements)
    hushcue_ct2.dll                        the engine bridge
    runtime\ctranslate2.dll, libiomp5md.dll   CTranslate2 4.8.2 from its PyPI wheel (CPU and GPU code)
    msvcp140*.dll, vcruntime140*.dll       the Visual C++ runtime, app-local, so no VC++ redistributable is needed
    ffmpeg\                                only with -BundleFfmpeg (off by default: see docs/shipping.md, licensing)
    THIRD-PARTY-NOTICES.md (and LICENSE when the repo has one)
cuBLAS is not included: the app downloads it on first run (Hushcue.Core.Pipeline.RuntimeDownload).

-SmokeTest starts the published Hushcue.exe, waits for its window and closes it again.
#>
param(
    [string]$Output = (Join-Path $PSScriptRoot "..\artifacts\publish\Hushcue"),
    [string]$Version,
    [string]$SitePackages = (Join-Path $PSScriptRoot "..\..\subgen\.venv\Lib\site-packages"),
    [string]$Ct2Version = "4.8.2",
    [string]$BundleFfmpeg,
    [switch]$NoReadyToRun,
    [switch]$SmokeTest
)
$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$bridge = Join-Path $root "native\bin\hushcue_ct2.dll"
if (-not (Test-Path $bridge)) { throw "native\bin\hushcue_ct2.dll is missing: run native\build.ps1 first" }

# --- CTranslate2, from the same wheel the bridge was built against
$ct2 = Join-Path $SitePackages "ctranslate2"
$installed = Get-ChildItem $SitePackages -Directory -Filter "ctranslate2-*.dist-info" -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $installed -or $installed.Name -ne "ctranslate2-$Ct2Version.dist-info") {
    throw "Expected ctranslate2 $Ct2Version in $SitePackages, found $($installed.Name)"
}
$runtimeDlls = @("ctranslate2.dll", "libiomp5md.dll") | ForEach-Object { Join-Path $ct2 $_ }
foreach ($dll in $runtimeDlls) { if (-not (Test-Path $dll)) { throw "Missing $dll" } }

# --- Visual Studio: the C++ runtime to ship app-local, and dumpbin to check it is new enough
$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw "Visual Studio Build Tools with the C++ workload are not installed" }
$msvc = Get-ChildItem (Join-Path $vs "VC\Tools\MSVC") -Directory | Sort-Object { [version]$_.Name } | Select-Object -Last 1
$dumpbin = Join-Path $msvc.FullName "bin\Hostx64\x64\dumpbin.exe"
$crt = Get-ChildItem (Join-Path $vs "VC\Redist\MSVC") -Directory |
    Where-Object { $_.Name -match '^\d+\.\d+\.\d+$' } | Sort-Object { [version]$_.Name } |
    ForEach-Object { Get-ChildItem (Join-Path $_.FullName "x64") -Directory -Filter "Microsoft.VC*.CRT" } |
    Select-Object -Last 1
if (-not $crt) { throw "The Visual C++ redistributable files were not found under $vs\VC\Redist\MSVC" }
$crtDlls = @("msvcp140.dll", "msvcp140_1.dll", "vcruntime140.dll", "vcruntime140_1.dll") | ForEach-Object { Join-Path $crt.FullName $_ }

# --- publish
$Output = [IO.Path]::GetFullPath($Output)
if (Test-Path $Output) { Remove-Item -Recurse -Force $Output }
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = Join-Path $env:ProgramFiles "dotnet\dotnet.exe" }
$publishArgs = @(
    "publish", (Join-Path $root "src\Hushcue.App\Hushcue.App.csproj"),
    "-c", "Release", "-r", "win-x64", "--self-contained", "-o", $Output,
    # unpackaged apps only get their resources.pri (the compiled XAML) on publish with the MSIX tooling switched on
    "-p:EnableMsixTooling=true",
    "-p:PublishReadyToRun=$(if ($NoReadyToRun) { 'false' } else { 'true' })"
)
if ($Version) { $publishArgs += "-p:Version=$Version" }
& $dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
foreach ($required in @("Hushcue.exe", "Hushcue.pri", "hushcue_ct2.dll", "Assets\Hushcue.ico")) {
    if (-not (Test-Path (Join-Path $Output $required))) { throw "The publish output has no $required" }
}
# debug symbols and import libraries are not needed to run
Get-ChildItem $Output -Recurse -Include *.pdb, *.lib | Remove-Item

New-Item -ItemType Directory -Force (Join-Path $Output "runtime") | Out-Null
Copy-Item $runtimeDlls (Join-Path $Output "runtime")
Copy-Item $crtDlls $Output

# The app-local C++ runtime is found before the one in System32, so it must be at least as new as the toolset every
# native DLL was linked with (an older msvcp140.dll can crash code built with a newer compiler).
$crtVersion = [version]((Get-Item (Join-Path $Output "msvcp140.dll")).VersionInfo.ProductVersion -replace '^(\d+\.\d+).*', '$1')
$natives = @(Get-ChildItem $Output -Filter *.dll) + @(Get-ChildItem (Join-Path $Output "runtime") -Filter *.dll)
foreach ($dll in $natives) {
    $imports = & $dumpbin /nologo /dependents $dll.FullName | Out-String
    if ($imports -notmatch '(?i)msvcp140|vcruntime140') { continue }
    $headers = & $dumpbin /nologo /headers $dll.FullName | Out-String
    if ($headers -match '(\d+\.\d+) linker version' -and [version]$Matches[1] -gt $crtVersion) {
        throw "$($dll.Name) was linked with MSVC $($Matches[1]), newer than the C++ runtime being shipped ($crtVersion)"
    }
}

# --- optional ffmpeg
if ($BundleFfmpeg) {
    $source = Resolve-Path $BundleFfmpeg
    $bin = if (Test-Path (Join-Path $source "bin\ffmpeg.exe")) { Join-Path $source "bin" } else { $source }
    foreach ($exe in "ffmpeg.exe", "ffprobe.exe") {
        if (-not (Test-Path (Join-Path $bin $exe))) { throw "$exe not found in $BundleFfmpeg" }
    }
    $target = Join-Path $Output "ffmpeg"
    New-Item -ItemType Directory -Force $target | Out-Null
    Copy-Item (Join-Path $bin "ffmpeg.exe"), (Join-Path $bin "ffprobe.exe") $target
    Get-ChildItem $bin -Filter *.dll | Copy-Item -Destination $target   # shared builds
    Get-ChildItem $source -File | Where-Object { $_.Name -match '^(LICENSE|COPYING|README)' } | Copy-Item -Destination $target
    $config = & (Join-Path $target "ffmpeg.exe") -hide_banner -version | Select-String "configuration:"
    if ("$config" -match "--enable-gpl") {
        Write-Warning "This ffmpeg is a GPL build: the package must then follow the GPL for ffmpeg (docs/shipping.md)."
    }
    if (-not (Get-ChildItem $target -File | Where-Object { $_.Name -match '^(LICENSE|COPYING)' })) {
        Write-Warning "No LICENSE file found next to ffmpeg in $BundleFfmpeg; add ffmpeg's licence text to the package."
    }
}

# --- notices: ours, plus the upstream licence and notice files from the NuGet packages this build restored
Copy-Item (Join-Path $root "THIRD-PARTY-NOTICES.md") $Output
if (Test-Path (Join-Path $root "LICENSE")) { Copy-Item (Join-Path $root "LICENSE") $Output }
$licenses = Join-Path $Output "licenses"
New-Item -ItemType Directory -Force $licenses | Out-Null
Copy-Item (Join-Path $root "docs\licenses\*") $licenses
$assets = Get-Content (Join-Path $root "src\Hushcue.App\obj\project.assets.json") -Raw | ConvertFrom-Json
$packageFolder = @($assets.packageFolders.PSObject.Properties.Name)[0]
$notices = [ordered]@{
    "Microsoft.WindowsAppSDK.WinUI" = @{ "license.txt" = "WindowsAppSDK-license.txt"; "NOTICE.txt" = "WindowsAppSDK-NOTICE.txt" }
    "Microsoft.ML.OnnxRuntime" = @{ "ThirdPartyNotices.txt" = "OnnxRuntime-ThirdPartyNotices.txt" }
    "Microsoft.NETCore.App.Runtime.win-x64" = @{ "THIRD-PARTY-NOTICES.TXT" = "dotnet-THIRD-PARTY-NOTICES.txt" }
}
foreach ($package in $notices.Keys) {
    $library = $assets.libraries.PSObject.Properties | Where-Object { $_.Name -like "$package/*" } | Select-Object -First 1
    if (-not $library) {
        # the runtime pack is not in the assets file; take the version this publish used
        $runtimeVersion = (Get-Content (Join-Path $Output "Hushcue.runtimeconfig.json") -Raw | ConvertFrom-Json).runtimeOptions.includedFrameworks[0].version
        $folder = Join-Path $packageFolder "$($package.ToLowerInvariant())\$runtimeVersion"
    }
    else {
        $folder = Join-Path $packageFolder $library.Value.path
    }
    foreach ($file in $notices[$package].Keys) {
        $source = Join-Path $folder $file
        if (-not (Test-Path $source)) { throw "Missing $source" }
        Copy-Item $source (Join-Path $licenses $notices[$package][$file])
    }
}

$files = Get-ChildItem $Output -Recurse -File
Write-Host ("Published {0} files, {1:N0} MB, to {2}" -f $files.Count, (($files | Measure-Object Length -Sum).Sum / 1MB), $Output)

# --- smoke test: the window opens and stays up
if ($SmokeTest) {
    $process = Start-Process (Join-Path $Output "Hushcue.exe") -PassThru
    try {
        $clock = [Diagnostics.Stopwatch]::StartNew()
        while ($process.MainWindowHandle -eq 0 -and -not $process.HasExited -and $clock.Elapsed.TotalSeconds -lt 30) {
            Start-Sleep -Milliseconds 20
            $process.Refresh()
        }
        if ($process.HasExited) { throw "Hushcue.exe exited during startup (code $($process.ExitCode))" }
        if ($process.MainWindowHandle -eq 0) { throw "Hushcue.exe showed no window within 30 s" }
        $opened = $clock.ElapsedMilliseconds
        Start-Sleep -Seconds 2
        $process.Refresh()
        if ($process.HasExited) { throw "Hushcue.exe exited after opening its window (code $($process.ExitCode))" }
        Write-Host "Smoke test: window '$($process.MainWindowTitle)' opened after $opened ms"
    }
    finally {
        if (-not $process.HasExited) {
            $null = $process.CloseMainWindow()
            if (-not $process.WaitForExit(10000)) { $process.Kill() }
        }
    }
}
