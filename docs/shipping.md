# Shipping Hushcue (Phase 8)

How a release is built, what goes in it, what is downloaded on first run, and the decisions still open (ffmpeg and
the licence). Measured on the development PC (Windows 11, RTX 4070) in October 2026.

## What a release is

| File | What it is | Size |
|---|---|---|
| `Hushcue-<version>-setup.exe` | Inno Setup installer, per-user by default (no admin) | 66 MB |
| `Hushcue-<version>-win-x64.zip` | the same files, portable: unzip and run `Hushcue.exe` | 102 MB |
| `SHA256SUMS.txt` | checksums of the two | |

Installed, the app folder is 290 MB (470 files). Not in it, and fetched by the app when needed:

| What | Size | Where it goes |
|---|---|---|
| Speech model (large-v3-turbo; large-v3 for the Best preset) | 1.6 GB (3.1 GB) | `%LOCALAPPDATA%\Hushcue\models` (`Pipeline/ModelDownload.cs`) |
| GPU libraries: cuBLAS and NVRTC, only with an NVIDIA GPU | 630 MB download, 870 MB unpacked | `%LOCALAPPDATA%\Hushcue\runtime` (`Pipeline/RuntimeDownload.cs`) |
| ffmpeg / ffprobe | | the user's own install on PATH, unless bundled (see [ffmpeg](#ffmpeg)) |

## Building one

Locally (after `native\build.ps1`):

```powershell
powershell -ExecutionPolicy Bypass -File tools\publish.ps1 -Version 0.2.0 -SmokeTest   # -> artifacts\publish\Hushcue
powershell -ExecutionPolicy Bypass -File installer\build.ps1 -Version 0.2.0 -Iscc <path to ISCC.exe>   # -> artifacts\installer
```

`publish.ps1` takes `-SitePackages` (where the `ctranslate2==4.8.2` wheel is installed, SubGen's venv by default),
`-Output`, `-BundleFfmpeg <folder>` and `-NoReadyToRun`; `-SmokeTest` starts the published `Hushcue.exe`, waits for
its window and closes it. Inno Setup does not need installing: `innosetup-6.7.3.exe /VERYSILENT /PORTABLE=1
/CURRENTUSER /DIR=<folder>` unpacks a portable copy with no registry entries.

On GitHub: push a tag `v0.2.0` (or run the **Release** workflow by hand with a version). `.github/workflows/release.yml`
sets up the same way as CI (CTranslate2 wheel, bridge with `-CpuOnly`), builds and tests, publishes, unpacks a pinned
Inno Setup 6.7.3 (SHA-256 checked), builds the installer and zip, and attaches them with `SHA256SUMS.txt` to a
**draft** release to check and publish by hand. `-CpuOnly` is right for releases too: the bridge is identical either
way (GPU support lives in `ctranslate2.dll`); the flag only skips gathering cuBLAS on the build machine. The
workflow has not been run yet (it can't be from here); the steps it runs were each run locally, except the `gh`
release step.

## The publish

Self-contained win-x64, unpackaged, with the Windows App SDK runtime copied next to the exe. Two things it needs
that a plain `dotnet publish` gets wrong:

- **`EnableMsixTooling=true`.** Without it an unpackaged publish has no `Hushcue.pri` (the compiled XAML), and the app
  dies at start with `XamlParseException: Cannot locate resource from 'ms-appx:///MainWindow.xaml'` (exit code
  0xC000027B). `dotnet build` output is fine; only publish is affected. The script passes it; the project file is
  untouched.
- **The Visual C++ runtime, app-local.** `onnxruntime.dll`, `hushcue_ct2.dll`, `ctranslate2.dll` and
  `libiomp5md.dll` need `msvcp140.dll`, `msvcp140_1.dll`, `vcruntime140.dll` and `vcruntime140_1.dll`, which a clean
  Windows does not have (the Windows App SDK links its own statically). The script copies them from Visual Studio's
  `VC\Redist` folder next to `Hushcue.exe`, where they are found before any copy in System32, and fails if any native
  DLL was linked with a newer MSVC than the runtime it ships (today all are 14.44 or older, and the runtime is 14.44).

### Trimming and ReadyToRun

| Publish | Folder size* | Files | Window opens, first start | Window opens, later starts | Result |
|---|---|---|---|---|---|
| plain | 187 MB | 461 | 808 ms | 314–327 ms | works |
| **ReadyToRun** | **231 MB** | 461 | **660 ms** | **237–239 ms** | **works: adopted** |
| trimmed | 99 MB | 309 | | | crashes at start |
| trimmed + ReadyToRun | 109 MB | 309 | | | crashes at start |

\* before adding `runtime\`, the C++ runtime and notices (+60 MB). Time from `Start-Process` until the process has a
main window, 6 runs each; the first run is after the publish, with a warm disk cache.

ReadyToRun opens the window about 25% sooner for 44 MB more (10 MB more in the installer). Trimming halves the .NET
part but is not usable yet: the build reports trim warnings from `WinRT.Runtime` and `Microsoft.Windows.SDK.NET`
(IL2104) and from `AppSettings` (reflection-based `System.Text.Json`, IL2026; trimmed apps turn reflection JSON off,
so settings would throw), and with the warnings silenced the app crashes in `Microsoft.UI.Xaml.dll` at start. Native
AOT needs everything trimming needs and more; not tried. To revisit: a `JsonSerializerContext` for `AppSettings`,
then CsWinRT's AOT/trimming support for WinUI.

### Where the libraries are found

| Library | Shipped in | Found by |
|---|---|---|
| `hushcue_ct2.dll` | app folder | `[LibraryImport]`, next to the exe |
| `ctranslate2.dll`, `libiomp5md.dll` | `runtime\` | `RuntimeLibraries` puts `<app>\runtime` on PATH |
| `cublas64_12.dll`, `cublasLt64_12.dll`, `nvrtc*.dll` | downloaded to `%LOCALAPPDATA%\Hushcue\runtime` | the same, second on PATH; CTranslate2 loads cuBLAS with `LoadLibrary` the first time it needs it |
| `msvcp140*.dll`, `vcruntime140*.dll` | app folder | the exe's folder comes first in the DLL search |
| ffmpeg | `ffmpeg\` only when bundled | `Tools.Find`: `<app>\ffmpeg\` then PATH |

Checked by running the CLI from a copy of the published folder, with the development runtime folder moved aside and
modules listed while it ran: on the GPU (cuBLAS freshly downloaded by `RuntimeDownload`) it loaded `ctranslate2.dll`
and `libiomp5md.dll` from `<app>\runtime`, the C++ runtime from `<app>`, cuBLAS from `%LOCALAPPDATA%\Hushcue\runtime`,
and gave the same 85 words as the development build. With nothing downloaded, `--device cpu` works from the app
folder alone, and `--device cuda` fails with `Library cublas64_12.dll is not found or cannot be loaded`, which
`SubtitleJob` treats as a GPU error and retries on the CPU.

One quirk: when `CUDA_PATH` is set (a CUDA Toolkit is installed), CTranslate2 calls `SetDllDirectory(CUDA_PATH\bin)`
before loading cuBLAS, so the toolkit's `cublas64_12.dll` wins over the downloaded one. Any CUDA 12 cuBLAS works, so
that is harmless, but `RuntimeDownload.Check()` still reports the download as missing on such a PC.

## GPU libraries on first run

The installer used to be expected to carry cuBLAS and cuDNN (about 1.8 GB). Measured instead:

- The CTranslate2 4.8.2 Windows wheel is built with `WITH_CUDNN=OFF` and `CUDA_DYNAMIC_LOADING=ON`
  (`python/tools/prepare_build_environment_windows.sh` in its repo). It never loads cuDNN: a GPU transcription loads
  `cublas64_12.dll` and `cublasLt64_12.dll` and nothing else from NVIDIA's libraries, and runs with only those two
  present. **cuDNN is not needed at all**, which saves 1.1 GB.
- `cublasLt64_12.dll` refers to `nvrtc64_120_0.dll`, which it can load for some kernels, and the cuBLAS wheel
  declares a dependency on NVRTC. It was not loaded on the RTX 4070, but it is downloaded too (76 MB) so other GPUs
  don't fail.

So the app downloads two wheels from PyPI, the ones pip picks for SubGen's environment and the exact DLLs SubGen runs
with (same SHA-256 as `subgen\.venv`):

| Wheel | Size | Files taken | Unpacked |
|---|---|---|---|
| `nvidia_cublas_cu12-12.9.2.10-py3-none-win_amd64.whl` | 553 MB | `cublas64_12.dll`, `cublasLt64_12.dll`, `License.txt` (as `NVIDIA-CUDA-LICENSE.txt`) | 771 MB |
| `nvidia_cuda_nvrtc_cu12-12.9.86-py3-none-win_amd64.whl` | 76 MB | `nvrtc64_120_0.dll`, `nvrtc-builtins64_129.dll` | 97 MB |

Each wheel is pinned by URL (files.pythonhosted.org), size and SHA-256, and each file taken out of it by size and
SHA-256 (`RuntimeDownload.GpuPackages`). The real download was run once into a scratch folder through the internal
API, cancelled at 150 MB and resumed: it finished, all checksums matched, and unpacking took about 3 s. Tests
(`RuntimeDownloadTests`) use a local `HttpListener` and small fake wheels: unpacking only the listed files, resume
with a range request, a server that ignores ranges, wrong wheel checksum, wrong file inside a wheel, cancelling, and
the status check.

To move to newer versions: install them in a venv, check a transcription, take the URL, size and SHA-256 from
`https://pypi.org/pypi/<package>/<version>/json` and each DLL's size and SHA-256 from the wheel, and update
`GpuPackages`.

### API for the setup screen

```csharp
using Hushcue.Core.Engine;
using Hushcue.Core.Pipeline;

var status = RuntimeDownload.Check();              // fast: looks for each DLL with the right size, no hashing
var hasNvidiaGpu = WhisperModel.CudaDeviceCount() > 0; // works without cuBLAS (needs only the NVIDIA driver)
if (hasNvidiaGpu && !status.IsReady)
{
    // offer "Download GPU support (status.DownloadSize bytes)"; without it the app still works, on the CPU
    await RuntimeDownload.DownloadAsync((done, total) => /* marshal to the UI thread */, cancel);
}
```

- `RuntimeStatus`: `IsReady`, `Missing` (the packages still needed), `DownloadSize` (bytes to fetch).
- `DownloadAsync(Action<long, long> progress, CancellationToken)`: progress is bytes downloaded and the total, from a
  worker thread, at least every 8 MB; once it reaches the total the wheels are unpacked (a few seconds) before the
  task completes. It does nothing when everything is there.
- Errors: `RuntimeDownloadException` with a message meant for the user (network, disk full: it checks free space
  first, about 1.5 GB, checksum); `OperationCanceledException` when cancelled. Calling it again continues where it
  stopped: partial wheels are kept in `%LOCALAPPDATA%\Hushcue\runtime.download`, deleted once unpacked.
- No restart needed: the folder is on the DLL search path from the start, and CTranslate2 tries to load cuBLAS again
  on its next GPU run if an earlier one failed.
- Same shape as `ModelDownload.DownloadAsync`, so one progress UI can serve both. Run one download at a time.

## Installer

`installer/hushcue.iss` (Inno Setup 6):

- Per-user by default into `%LOCALAPPDATA%\Programs\Hushcue` with no admin prompt; the first page offers an
  all-users install instead (that one asks for admin and writes HKLM).
- Start menu shortcut, optional desktop shortcut, uninstaller in Apps & features, "Launch Hushcue" at the end.
- Task, ticked by default: "Generate subtitles with Hushcue" on the right-click menu of the 18 types in
  `MediaFiles.Extensions` (`InstallerTests` keeps the lists in step), under `Software\Classes\SystemFileAssociations`,
  which adds a verb without changing what opens the file. The command is `"Hushcue.exe" "%1"`.
- An update replaces `runtime\`; uninstalling removes everything it wrote. An interactive uninstall asks whether to
  delete `%LOCALAPPDATA%\Hushcue` (models, GPU libraries, settings), default No; a silent one keeps it.
- Shows `LICENSE` as a licence page once the repo has one.

Tested here: built with a portable Inno Setup 6.7.3, installed silently per-user; the shortcut and 18 verbs were
there; the app started from the Start menu shortcut, and from the Explorer verb on an `.mp4` (`Start-Process -Verb
Hushcue.Generate`) with the file in its queue; uninstalled silently: the folder, shortcut, uninstall entry and
registry keys were gone (registry under `SystemFileAssociations` identical to before) and `%LOCALAPPDATA%\Hushcue`
was kept.

Known limits:

- Selecting several files and choosing the verb starts one Hushcue per file (Explorer runs a `%1` command once per
  item). Fix in the app: make it single-instance (`AppInstance.FindOrRegisterForKey` + `RedirectActivationToAsync`)
  and add the files to the running window's queue.
- Uninstalling while Hushcue is running leaves the files that are in use. The app could create a named mutex for
  the installer's `AppMutex` to detect.
- Not code-signed: SmartScreen warns on the installer and the exe until the files gain reputation. Options: a
  code-signing certificate, or Azure Trusted Signing; either plugs into `installer\build.ps1` (Inno's `SignTool`).

## ffmpeg

Hushcue runs `ffmpeg` and `ffprobe` as separate processes (`Media/Tools.cs`: `<app>\ffmpeg\` first, then PATH). Two
ways to ship, both supported; **the default today is the first**:

1. **Don't bundle (default).** The user installs ffmpeg (`winget install Gyan.FFmpeg`), the app's error message says
   so. Nothing of ffmpeg's licence applies to the Hushcue package. The setup screen (Phase 6b) should check for it.
2. **Bundle** with `tools\publish.ps1 -BundleFfmpeg <folder>` (a folder with `ffmpeg.exe` and `ffprobe.exe`, or one
   with them in `bin\`): copied to `<app>\ffmpeg\` with its `LICENSE`/`README`; the script warns when the build is GPL.
   Size: Gyan's 9.0.2 full build adds 455 MB to the folder; the essentials build is smaller.

What bundling means, by build (this is a summary to decide with, not legal advice):

- **Gyan's builds, BtbN's `gpl` builds** include libx264/libx265 and are configured with `--enable-gpl`
  (`--enable-version3` too), so the ffmpeg binaries are under the GPL (v3 for Gyan's). Shipping them means shipping
  GPL software: include the licence, and offer the corresponding source (the exact ffmpeg source and build scripts,
  or a written offer) for as long as you distribute them. Because Hushcue only runs ffmpeg as a separate program and
  talks to it through command lines and pipes, Hushcue itself is generally regarded as a separate work and need not
  be GPL ("aggregate").
- **An LGPL build** (BtbN's `lgpl` builds, or your own without `--enable-gpl`): no libx264. Burn-in would then rely
  on NVENC (`h264_nvenc`, which `Embed` already prefers when the GPU has it) or another LGPL-compatible encoder; the
  CPU fallback to libx264 in `Embed` would need an alternative (for example `libopenh264`, BSD, or Media Foundation's
  `h264_mf`). Obligations: include the LGPL text and offer the source of the ffmpeg build.
- Either way, x264/x265 and AAC/H.264/HEVC carry patent-licensing questions in some countries that a licence alone
  does not settle.

## Choosing a licence

`LICENSE` is still to be chosen (Phase 0). Things that bear on it, from the packages above:

- Everything Hushcue links to is MIT or Apache-2.0 (CTranslate2, faster-whisper, Silero VAD, ONNX Runtime, .NET,
  oneDNN) or proprietary-but-redistributable: the Windows App SDK and the VC++ runtime (Microsoft), and Intel's
  OpenMP runtime and the oneMKL code inside `ctranslate2.dll`. Those three require the app to be distributed under
  terms that protect them at least as much as their own licences: no reverse engineering of those binaries, use only
  as part of the app. A permissive licence (MIT, Apache-2.0) for Hushcue plus a short end-user licence covering the
  bundled third-party binaries fits that. A GPL licence for Hushcue would sit awkwardly with them: they are not
  "system libraries", and both Microsoft's and Intel's terms forbid making their code subject to copyleft terms.
- NVIDIA's cuBLAS/NVRTC are not shipped: the user's PC downloads them from NVIDIA's own PyPI packages, and their
  licence is saved beside them. If they were ever bundled instead, they are on the CUDA EULA's redistributable list
  (Attachment A), with conditions (part of an application with material extra functionality; terms consistent with
  NVIDIA's EULA).
- Model weights: Whisper's are MIT, as are the CTranslate2 conversions the app downloads.
- `THIRD-PARTY-NOTICES.md` (shipped with the app, with the upstream notice files in `licenses\`) lists all of this.
