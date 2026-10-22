# Third-party notices

Hushcue itself is MIT licensed (`LICENSE`, copyright Jatinjit Singh). The rest of this file is about the work of others
it ships with or downloads.

Hushcue is built on the software listed below. This file ships with the app; the longer upstream notice files sit
next to it in the `licenses` folder. Versions are the ones this release is built with.

## Shipped with Hushcue

| Component | Version | Licence | Where it is |
|---|---|---|---|
| [CTranslate2](https://github.com/OpenNMT/CTranslate2) | 4.8.2 | MIT | `runtime\ctranslate2.dll` (from the PyPI wheel) |
| ↳ Intel oneMKL, statically linked | 2025.3 | Intel End User License Agreement for Developer Tools (redistributable) | inside `ctranslate2.dll` |
| ↳ [oneDNN](https://github.com/uxlfoundation/oneDNN), statically linked | 3.1.1 | Apache-2.0 | inside `ctranslate2.dll` |
| ↳ [cpu_features](https://github.com/google/cpu_features) | | Apache-2.0 | inside `ctranslate2.dll` |
| ↳ [spdlog](https://github.com/gabime/spdlog), with [{fmt}](https://github.com/fmtlib/fmt) | | MIT | inside `ctranslate2.dll` |
| ↳ NVIDIA CUDA runtime (static), Thrust/CUB | CUDA 12.8 | NVIDIA CUDA EULA (distributable: `cudart_static.lib`); Apache-2.0 with LLVM exception | inside `ctranslate2.dll` |
| Intel OpenMP runtime | 2025.3 | Intel End User License Agreement for Developer Tools (redistributable) | `runtime\libiomp5md.dll` |
| [faster-whisper](https://github.com/SYSTRAN/faster-whisper) | 1.2.1 | MIT | ported to C# (`Hushcue.Core.dll`, `Engine\`) |
| [Silero VAD](https://github.com/snakers4/silero-vad) model, as shipped by faster-whisper | v6 | MIT | embedded in `Hushcue.Core.dll` |
| [ONNX Runtime](https://github.com/microsoft/onnxruntime) | 1.31.0 | MIT | `onnxruntime.dll`, `Microsoft.ML.OnnxRuntime.dll` |
| [.NET runtime](https://github.com/dotnet/runtime) | 10.0 | MIT | the .NET files next to `Hushcue.exe` |
| [Windows App SDK](https://github.com/microsoft/WindowsAppSDK) (WinUI 3 and its runtime) | 2.x | Microsoft Software License Terms, distributable code | `Microsoft.*.dll`, `*.pri`, language folders |
| [C#/WinRT](https://github.com/microsoft/CsWinRT) and the Windows SDK projection | | MIT; Windows SDK licence | `WinRT.Runtime.dll`, `Microsoft.Windows.SDK.NET.dll` |
| Microsoft Visual C++ runtime | 14.4x | Visual Studio licence terms, distributable code | `msvcp140*.dll`, `vcruntime140*.dll` |

## Downloaded by Hushcue on first use (not shipped)

| Component | Licence | Where it comes from |
|---|---|---|
| Whisper large-v3 and large-v3-turbo model weights (OpenAI) | MIT ("Whisper's code and model weights are released under the MIT License") | CTranslate2 conversions on Hugging Face: `Systran/faster-whisper-large-v3`, `dropbox-dash/faster-whisper-large-v3-turbo` (both MIT) |
| NVIDIA cuBLAS 12.9.2.10 and NVRTC 12.9.86 (GPU only) | NVIDIA CUDA Toolkit EULA. `cublas.dll`, `cublasLt.dll`, `nvrtc.dll` and `nvrtc-builtins.dll` are listed as distributable in its Attachment A | NVIDIA's `nvidia-cublas-cu12` and `nvidia-cuda-nvrtc-cu12` wheels on PyPI; the licence is saved beside them as `NVIDIA-CUDA-LICENSE.txt` in `%LOCALAPPDATA%\Hushcue\runtime` |

## ffmpeg

Hushcue runs `ffmpeg` and `ffprobe` as separate programs. By default it uses the copy on `PATH` (for example from
`winget install Gyan.FFmpeg`) and ships none. A build made with `tools\publish.ps1 -BundleFfmpeg` includes one in the
`ffmpeg` folder, with its own licence file: ffmpeg is LGPL-2.1-or-later, or GPL-2.0-or-later / GPL-3.0 when built
with GPL parts such as libx264 (as Gyan's builds are). See [docs/shipping.md](docs/shipping.md#ffmpeg).

## Licence texts

### MIT License

Applies to CTranslate2, faster-whisper, Silero VAD, ONNX Runtime, .NET, C#/WinRT, spdlog, {fmt} and the Whisper
model weights, each with its own copyright notice:

- CTranslate2: Copyright (c) 2018- SYSTRAN. Copyright (c) 2019- The OpenNMT Authors.
- faster-whisper: Copyright (c) 2023 SYSTRAN
- Silero VAD: Copyright (c) 2020-present Silero Team
- ONNX Runtime: Copyright (c) Microsoft Corporation
- .NET: Copyright (c) .NET Foundation and Contributors
- C#/WinRT: Copyright (c) Microsoft Corporation
- spdlog: Copyright (c) 2016 - present, Gabi Melman and spdlog contributors
- {fmt}: Copyright (c) 2012 - present, Victor Zverovich and {fmt} contributors
- Whisper: Copyright (c) 2022 OpenAI

```
Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

### Apache License 2.0

oneDNN (Copyright 2016-2025 Intel Corporation and others) and cpu_features (Copyright Google LLC). Full text:
`licenses\Apache-2.0.txt`.

### Intel software

The Intel OpenMP runtime (`libiomp5md.dll`) and the oneMKL code linked into `ctranslate2.dll` are Intel
Redistributables under the Intel End User License Agreement for Developer Tools. They may only be used as part of
Hushcue, and you may not reverse engineer, decompile or disassemble them. The OpenMP runtime's own third-party
notices are in `licenses\Intel-OpenMP-third-party-programs.txt`.

### Microsoft software

The Windows App SDK and Visual C++ runtime files are Microsoft distributable code: you may use them only as part of
Hushcue, and they are licensed to you on terms that protect them and Microsoft at least as much as Microsoft's own
licence terms (no reverse engineering except where the law or an open-source component's licence allows it). The
Windows App SDK's licence and third-party notices, and the .NET and ONNX Runtime third-party notices, are in the
`licenses` folder.
